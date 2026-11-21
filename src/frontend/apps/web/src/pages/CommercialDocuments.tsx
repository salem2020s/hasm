import { ApiError, type Branch, type CommercialDocument, type CommercialDocumentInput, type CommercialStatus, type DocumentLink, type DocumentLinkKind, type InventoryItem, type LineQuantity, type PartyRole } from "@hasm/api-client";
import { Tabs, useI18n, type TabItem } from "@hasm/ui";
import { useCallback, useEffect, useMemo, useRef, useState, type FormEvent } from "react";
import { useAuth } from "../auth";
import { Banner, Card, ConfirmDialog, errorText, Field, Loading, PageHeader } from "../components";
import { parsePositiveDecimal } from "../numberInput";
import { useReportUnsaved, useUnsaved } from "../unsaved";
import { blankToNull, invName } from "./inventoryCommon";

/**
 * Commercial documents (Phase 3.13 S6, owner decision D-079): the drafts of the commercial documents and their life cycle (submit, withdraw, approve, reject, cancel) with the official number and the server-sealed totals.
 * ONE implementation for Web and Windows Desktop. The screen only DRIVES the server: authorization (branch-scoped permissions, separation of duties), validation, pricing, taxes, numbering, the fiscal period, the life
 * cycle and audit are enforced by the server and PostgreSQL; a button is hidden when the user lacks the permission, which is a convenience, never the security. It works from the server's registry of document types:
 * in production none is activated (the concrete types arrive with 3.15/3.16), and the screen then shows "no document types activated" and offers no creation. The editor follows the shared Tabs standard (Document,
 * Lines, History); the document identity and the workflow actions stay visible on every tab and switching tabs never saves or resets input. Physical and accounting tabs belong to slices S3/S4 and the Links tab
 * to the S2 API after it merges; none is shown as a placeholder.
 */
const PAGE = 25;
const STATUSES: CommercialStatus[] = [1, 2, 3, 4, 5];
const todayIso = () => { const d = new Date(); const p = (n: number) => String(n).padStart(2, "0"); return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}`; };
const money = (n: number | null) => (n === null ? "—" : n.toLocaleString("en-US", { minimumFractionDigits: 2, maximumFractionDigits: 6, useGrouping: false }));
const newKey = () => (typeof crypto !== "undefined" && "randomUUID" in crypto ? crypto.randomUUID() : `k-${Date.now()}-${Math.random().toString(16).slice(2)}`);

interface UnitRef { id: string; code: string }
interface ItemRef { id: string; code: string; nameAr: string | null; nameEn: string | null; units: UnitRef[]; defaultUnitId: string }
interface LineRow { key: number; item: ItemRef | null; unitId: string; quantity: string }
interface PartyRef { id: string; code: string; name: string }
interface Form { docType: string; branchId: string; party: PartyRef | null; currency: string; date: string; mode: 1 | 2; note: string; lines: LineRow[] }
let rowKey = 0;
const snapshot = (f: Form) => JSON.stringify({ ...f, party: f.party?.id ?? "", lines: f.lines.map((l) => [l.item?.id ?? "", l.unitId, l.quantity]) });
type Editor = { mode: "new"; docType: string } | { mode: "edit"; id: string };
const statusChip = (s: number) => `chip ${s === 3 ? "chip-ok" : s >= 4 ? "chip-off" : ""}`;
const shortId = (id: string) => id.slice(0, 8);
const branchLabel = (b: Branch | undefined, id: string, locale: string) => (b ? `${b.code} — ${invName(b, locale)}` : shortId(id));

/** The user holds the permission company-wide or in some branch; the server decides for the document's own branch. */
function useCan() {
  const auth = useAuth();
  return (p: string) => auth.can(p) || auth.canAny(p);
}

export function CommercialDocumentsPage() {
  const { t, locale } = useI18n();
  const tRef = useRef(t);
  tRef.current = t;
  const auth = useAuth();
  const can = useCan();
  const { guard } = useUnsaved();
  const [types, setTypes] = useState<string[] | null>(null);
  const [branches, setBranches] = useState<Branch[]>([]);
  const [docs, setDocs] = useState<CommercialDocument[] | null>(null);
  const [parties, setParties] = useState<Map<string, PartyRole>>(new Map());
  const [loadError, setLoadError] = useState<string | null>(null);
  const [type, setType] = useState("");
  const [status, setStatus] = useState("");
  const [branch, setBranch] = useState("");
  const [offset, setOffset] = useState(0);
  const [editor, setEditor] = useState<Editor | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  useEffect(() => {
    void (async () => {
      try { setTypes(await auth.api.listCommercialDocumentTypes()); } catch (e) { if (!auth.handleError(e)) setLoadError(errorText(tRef.current, e, "commdoc.error.")); }
      try { setBranches((await auth.api.listBranches()).branches); } catch (e) { if (auth.handleError(e)) return; setBranches([]); }   // the branch names are a convenience: without the branch permission the code is shown short
    })();
  }, [auth.api, auth.handleError]);

  const loadDocs = useCallback(async () => {
    try {
      const list = await auth.api.listCommercialDocuments({ status: status ? (Number(status) as CommercialStatus) : undefined, docType: type || undefined, branchId: branch || undefined, offset, limit: PAGE });
      setDocs(list); setLoadError(null);
      const ids = [...new Set(list.map((d) => d.partyId))];
      if (ids.length > 0) {
        try { setParties(new Map((await auth.api.listPartyRoles(undefined, { ids })).items.map((r) => [r.partyId, r]))); } catch (e) { if (auth.handleError(e)) return; setParties(new Map()); }
      }
    } catch (e) { if (!auth.handleError(e)) setLoadError(errorText(tRef.current, e, "commdoc.error.")); }
  }, [auth.api, auth.handleError, status, type, branch, offset]);
  useEffect(() => { const h = setTimeout(() => void loadDocs(), 150); return () => clearTimeout(h); }, [loadDocs]);

  const branchById = useMemo(() => new Map(branches.map((b) => [b.id, b])), [branches]);
  if (loadError) return <Banner kind="error">{loadError}</Banner>;
  if (!types || !docs) return <Loading />;

  if (editor) {
    return (
      <DocumentEditor key={editor.mode === "edit" ? editor.id : `new-${editor.docType}`} editor={editor} types={types} branches={branches}
        onClose={() => guard(() => { setEditor(null); void loadDocs(); })} onChanged={(m) => { setNotice(m); void loadDocs(); }} onSwitch={(id) => setEditor({ mode: "edit", id })} />
    );
  }
  const canCreate = types.length > 0 && can("company.commercialdocs.create");
  const partyName = (id: string) => parties.get(id)?.partyName ?? shortId(id);

  return (
    <section aria-labelledby="page-title">
      <PageHeader title={t("commdoc.title")} description={t("commdoc.subtitle")}
        actions={canCreate && <button type="button" className="primary" onClick={() => guard(() => { setNotice(null); setEditor({ mode: "new", docType: types[0]! }); })}>{t("commdoc.new")}</button>} />
      {types.length === 0 && <Banner kind="info" testId="no-types">{t("commdoc.none_activated")}</Banner>}
      {types.length > 0 && !can("company.commercialdocs.create") && <Banner kind="info" testId="read-only">{t("commdoc.read_only")}</Banner>}
      <Banner kind="info" testId="no-effect-note">{t("commdoc.no_effect_note")}</Banner>
      {notice && <Banner kind="success" testId="saved">{notice}</Banner>}
      <Card title={t("commdoc.list")}>
        <div className="toolbar">
          <Field id="cd-type" label={t("commdoc.type")}>
            {(aria) => <select {...aria} value={type} onChange={(e) => { setOffset(0); setType(e.target.value); }}><option value="">{t("commdoc.filter_all")}</option>{[...new Set([...types, ...docs.map((d) => d.docType)])].map((x) => <option key={x} value={x}>{x}</option>)}</select>}
          </Field>
          <Field id="cd-status" label={t("commdoc.status")}>
            {(aria) => <select {...aria} value={status} onChange={(e) => { setOffset(0); setStatus(e.target.value); }}><option value="">{t("commdoc.filter_all")}</option>{STATUSES.map((x) => <option key={x} value={x}>{t(`commdoc.status.${x}`)}</option>)}</select>}
          </Field>
          <Field id="cd-branch" label={t("commdoc.branch")}>
            {(aria) => <select {...aria} value={branch} onChange={(e) => { setOffset(0); setBranch(e.target.value); }}><option value="">{t("commdoc.filter_all")}</option>{branches.map((b) => <option key={b.id} value={b.id}>{branchLabel(b, b.id, locale)}</option>)}</select>}
          </Field>
        </div>
        <div className="table-wrap">
          <table className="data-table">
            <caption className="visually-hidden">{t("commdoc.list")}</caption>
            <thead><tr><th scope="col">{t("commdoc.number")}</th><th scope="col">{t("commdoc.type")}</th><th scope="col">{t("commdoc.date")}</th><th scope="col">{t("commdoc.branch")}</th><th scope="col">{t("commdoc.party")}</th><th scope="col">{t("commdoc.total_gross")}</th><th scope="col">{t("commdoc.status")}</th><th scope="col" /></tr></thead>
            <tbody>
              {docs.length === 0 && <tr><td colSpan={8}>{!type && !status && !branch ? t("commdoc.none") : t("commdoc.none_filtered")}</td></tr>}
              {docs.map((d) => (
                <tr key={d.id} data-testid={`doc-${d.id}`}>
                  <td><bdi dir="ltr">{d.officialNumber ?? t("commdoc.unnumbered")}</bdi></td><td><bdi dir="ltr">{d.docType}</bdi></td><td><bdi dir="ltr">{d.documentDate}</bdi></td>
                  <td><bdi>{branchLabel(branchById.get(d.branchId), d.branchId, locale)}</bdi></td><td><bdi>{partyName(d.partyId)}</bdi></td><td className="num"><bdi dir="ltr">{money(d.totalGross)}</bdi></td>
                  <td><span className={statusChip(d.status)}>{t(`commdoc.status.${d.status}`)}</span></td>
                  <td><button type="button" onClick={() => guard(() => { setNotice(null); setEditor({ mode: "edit", id: d.id }); })}>{t("commdoc.view")}</button></td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
        <div className="toolbar" data-testid="paging">
          <button type="button" disabled={offset === 0} onClick={() => setOffset(Math.max(0, offset - PAGE))}>{t("inv.items.previous")}</button>
          <span aria-live="polite">{t("commdoc.paging", { from: String(docs.length === 0 ? 0 : offset + 1), to: String(offset + docs.length) })}</span>
          <button type="button" disabled={docs.length < PAGE} onClick={() => setOffset(offset + PAGE)}>{t("inv.items.next")}</button>
        </div>
      </Card>
    </section>
  );
}

const emptyForm = (docType: string): Form => ({ docType, branchId: "", party: null, currency: "", date: todayIso(), mode: 1, note: "", lines: [{ key: ++rowKey, item: null, unitId: "", quantity: "" }] });
const TAB_OF = (field: string): "document" | "lines" => (field.startsWith("lines") ? "lines" : "document");

function DocumentEditor({ editor, types, branches, onClose, onChanged, onSwitch }: {
  editor: Editor; types: string[]; branches: Branch[]; onClose: () => void; onChanged: (m: string) => void; onSwitch: (id: string) => void;
}) {
  const { t, locale } = useI18n();
  const auth = useAuth();
  const can = useCan();
  const isNew = editor.mode === "new";
  const [doc, setDoc] = useState<CommercialDocument | null>(null);
  const [loading, setLoading] = useState(!isNew);
  const [form, setForm] = useState<Form>(() => emptyForm(editor.mode === "new" ? editor.docType : ""));
  const [base, setBase] = useState(() => snapshot(form));
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [error, setError] = useState<string | null>(null);
  const [conflict, setConflict] = useState(false);
  const [busy, setBusy] = useState(false);
  const [tab, setTab] = useState("document");
  const [dialog, setDialog] = useState<null | "reject" | "cancel">(null);
  const [reason, setReason] = useState("");
  const [reasonError, setReasonError] = useState<string | null>(null);
  const [confirmSubmit, setConfirmSubmit] = useState(false);
  const [key] = useState(newKey);   // one idempotency key per new-draft editor: a retry of the same save never creates a second document
  const [partyNames, setPartyNames] = useState<Map<string, string>>(new Map());

  const load = useCallback(async () => {
    if (editor.mode !== "edit") return;
    try {
      const d = await auth.api.getCommercialDocument(editor.id);
      const items = new Map<string, ItemRef>();
      for (const id of new Set(d.lines.map((l) => l.itemId))) {
        try { items.set(id, await toItemRef(auth.api, await auth.api.getInventoryItem(id))); } catch (e) { if (auth.handleError(e)) return; }
      }
      let party: PartyRef = { id: d.partyId, code: shortId(d.partyId), name: shortId(d.partyId) };
      try { const r = (await auth.api.listPartyRoles(undefined, { ids: [d.partyId] })).items[0]; if (r) party = { id: r.partyId, code: r.partyCode, name: r.partyName }; } catch (e) { if (auth.handleError(e)) return; }
      const f: Form = {
        docType: d.docType, branchId: d.branchId, party, currency: d.currency, date: d.documentDate, mode: d.mode, note: d.note ?? "",
        lines: d.lines.map((l) => ({ key: ++rowKey, item: items.get(l.itemId) ?? { id: l.itemId, code: shortId(l.itemId), nameAr: null, nameEn: null, units: [{ id: l.unitId, code: shortId(l.unitId) }], defaultUnitId: l.unitId }, unitId: l.unitId, quantity: String(l.quantity) })),
      };
      setPartyNames(new Map([[party.id, party.name]]));
      setDoc(d); setForm(f); setBase(snapshot(f)); setErrors({}); setConflict(false); setLoading(false);
    } catch (e) { if (!auth.handleError(e)) { setError(errorText(t, e, "commdoc.error.")); setLoading(false); } }
  }, [auth.api, auth.handleError, editor, t]);
  useEffect(() => { void load(); }, [load]);

  const dirty = snapshot(form) !== base;
  useReportUnsaved("commercial-document-form", dirty, t("commdoc.form_section"));
  const status = (doc?.status ?? 1) as CommercialStatus;
  const canEdit = isNew ? can("company.commercialdocs.create") : (status === 1 || status === 3 || status === 4) && can("company.commercialdocs.update");
  // D-066: the approver is never the creator, the last editor or the submitter (the server enforces it; the screen mirrors it to explain the disabled buttons)
  const mine = !!doc && [doc.createdBy, doc.lastEditedBy, doc.submittedBy].some((u) => u !== null && u === auth.user?.id);
  const set = <K extends keyof Form>(k: K, v: Form[K]) => setForm((f) => ({ ...f, [k]: v }));
  const activeBranches = branches.filter((b) => b.isActive || b.id === form.branchId);

  function validate(): Record<string, string> {
    const v: Record<string, string> = {};
    if (isNew && !form.docType) v.docType = t("commdoc.field.required");
    if (!form.branchId) v.branchId = t("commdoc.field.required");
    if (!form.party) v.partyId = t("commdoc.field.required");
    if (!/^[A-Za-z]{3}$/.test(form.currency.trim())) v.currency = t("commdoc.field.invalid_currency");
    if (!form.date) v.documentDate = t("commdoc.field.required");
    if (form.lines.length === 0) v.lines = t("commdoc.field.lines_required");
    form.lines.forEach((l, i) => {
      if (!l.item) v[`lines[${i}].itemId`] = t("commdoc.field.required");
      if (!l.unitId) v[`lines[${i}].unitId`] = t("commdoc.field.required");
      if (Number.isNaN(parsePositiveDecimal(l.quantity))) v[`lines[${i}].quantity`] = t("commdoc.field.invalid_quantity");   // Arabic-Indic digits and a decimal separator are accepted; a grouping separator ("1,500") is refused
    });
    return v;
  }
  const tabsWithErrors = (e: Record<string, string>) => new Set(Object.keys(e).map(TAB_OF));
  const toInput = (): CommercialDocumentInput => ({
    docType: form.docType, branchId: form.branchId, partyId: form.party!.id, currency: form.currency.trim().toUpperCase(), documentDate: form.date, mode: form.mode, note: blankToNull(form.note), idempotencyKey: key,
    lines: form.lines.map((l) => ({ itemId: l.item!.id, unitId: l.unitId, quantity: parsePositiveDecimal(l.quantity) })),
  });

  function showServerError(err: unknown) {
    if (auth.handleError(err)) return;
    if (err instanceof ApiError && err.status === 412) { setConflict(true); return; }
    if (err instanceof ApiError && err.status === 400) {
      const out: Record<string, string> = {};
      for (const f of err.details.fields ?? []) { const k = `commdoc.field.${f.code}`; out[f.field] = t(k) === k ? t("field.invalid") : t(k); }
      setErrors(out); setError(t("form.fix_errors"));
      const first = (["document", "lines"] as const).find((id) => tabsWithErrors(out).has(id));
      if (first) setTab(first);
      return;
    }
    setError(errorText(t, err, "commdoc.error."));
  }

  async function save(e: FormEvent) {
    e.preventDefault();
    if (!canEdit) return;
    const v = validate();
    setErrors(v);
    if (Object.keys(v).length > 0) { setError(t("form.fix_errors")); const first = (["document", "lines"] as const).find((id) => tabsWithErrors(v).has(id)); if (first) setTab(first); return; }
    setBusy(true); setError(null); setConflict(false);
    try {
      if (editor.mode === "new") { const d = await auth.api.createCommercialDocument(toInput()); onChanged(t("commdoc.created")); setBase(snapshot(form)); onSwitch(d.id); }
      else { await auth.api.updateCommercialDocument(editor.id, toInput(), doc!.revision); setBase(snapshot(form)); onChanged(t("commdoc.saved")); await load(); }
    } catch (err) { showServerError(err); } finally { setBusy(false); }
  }

  async function act(run: () => Promise<unknown>, message: string) {
    setBusy(true); setError(null); setConflict(false); setDialog(null); setConfirmSubmit(false);
    try { await run(); onChanged(message); await load(); setReason(""); } catch (err) { showServerError(err); } finally { setBusy(false); }
  }

  if (loading) return <Loading />;
  const invalid = tabsWithErrors(errors);
  const rev = doc?.revision ?? 0;
  const sealed = !!doc && doc.totalGross !== null;
  const tabs: TabItem[] = [
    {
      id: "document", label: t("commdoc.tab.document"), invalid: invalid.has("document"),
      content: (
        <div className="fields">
          <Field id="cd-doctype" label={t("commdoc.type")} error={errors.docType} required>
            {(aria) => isNew
              ? <select {...aria} value={form.docType} disabled={!canEdit} onChange={(e) => set("docType", e.target.value)}>{types.map((x) => <option key={x} value={x}>{x}</option>)}</select>
              : <input {...aria} readOnly value={form.docType} dir="ltr" />}
          </Field>
          <Field id="cd-branch" label={t("commdoc.branch")} error={errors.branchId} required>
            {(aria) => <select {...aria} value={form.branchId} disabled={!canEdit || !isNew} onChange={(e) => set("branchId", e.target.value)}><option value="">{t("form.select")}</option>{activeBranches.map((b) => <option key={b.id} value={b.id}>{branchLabel(b, b.id, locale)}</option>)}</select>}
          </Field>
          <PartyPicker value={form.party} error={errors.partyId} disabled={!canEdit} onPick={(p) => { set("party", p); setPartyNames((m) => new Map(m).set(p.id, p.name)); }} />
          <Field id="cd-currency" label={t("commdoc.currency")} error={errors.currency} required hint={t("commdoc.currency_hint")}>
            {(aria) => <input {...aria} value={form.currency} readOnly={!canEdit} maxLength={3} dir="ltr" autoComplete="off" onChange={(e) => set("currency", e.target.value.toUpperCase())} />}
          </Field>
          <Field id="cd-date" label={t("commdoc.date")} error={errors.documentDate} required>{(aria) => <input {...aria} type="date" value={form.date} readOnly={!canEdit} dir="ltr" onChange={(e) => set("date", e.target.value)} />}</Field>
          <Field id="cd-mode" label={t("commdoc.mode")} error={errors.mode} hint={t("commdoc.mode_hint")}>
            {(aria) => <select {...aria} value={form.mode} disabled={!canEdit || (!!doc && doc.officialNumber !== null)} onChange={(e) => set("mode", Number(e.target.value) as 1 | 2)}><option value={1}>{t("commdoc.mode.1")}</option><option value={2}>{t("commdoc.mode.2")}</option></select>}
          </Field>
          <Field id="cd-note" label={t("commdoc.note")} error={errors.note}>{(aria) => <textarea {...aria} value={form.note} readOnly={!canEdit} rows={3} maxLength={500} onChange={(e) => set("note", e.target.value)} />}</Field>
          {doc && (
            <div data-testid="approval-panel">
              <Field id="cd-number" label={t("commdoc.number")}>{(aria) => <input {...aria} readOnly dir="ltr" value={doc.officialNumber ?? t("commdoc.unnumbered")} />}</Field>
              {doc.officialNumber === null && <p className="hint">{t("commdoc.not_numbered_yet")}</p>}
              {sealed && (
                <dl className="details" data-testid="totals">
                  <dt>{t("commdoc.totals")}</dt><dd />
                  <dt>{t("commdoc.total_net")}</dt><dd><bdi dir="ltr">{money(doc.totalNet)}</bdi></dd>
                  <dt>{t("commdoc.total_tax")}</dt><dd><bdi dir="ltr">{money(doc.totalTax)}</bdi></dd>
                  <dt>{t("commdoc.total_gross")}</dt><dd><bdi dir="ltr" data-testid="total-gross">{money(doc.totalGross)}</bdi></dd>
                  <dt>{t("commdoc.exchange_rate")}</dt><dd><bdi dir="ltr">{doc.exchangeRate ?? "—"}</bdi></dd>
                  {doc.approvedAtUtc && <><dt>{t("commdoc.approved_at")}</dt><dd><bdi dir="ltr">{doc.approvedAtUtc.slice(0, 19).replace("T", " ")}</bdi></dd></>}
                  {doc.sealedAtUtc && <><dt>{t("commdoc.sealed_at")}</dt><dd><bdi dir="ltr">{doc.sealedAtUtc.slice(0, 19).replace("T", " ")}</bdi></dd></>}
                </dl>
              )}
            </div>
          )}
        </div>
      ),
    },
    {
      id: "lines", label: t("commdoc.tab.lines"), invalid: invalid.has("lines"),
      content: (
        <div>
          {errors.lines && <Banner kind="error">{errors.lines}</Banner>}
          <p className="hint">{t("commdoc.lines_hint")}</p>
          <div className="table-wrap">
            <table className="data-table">
              <caption className="visually-hidden">{t("commdoc.tab.lines")}</caption>
              <thead><tr><th scope="col">#</th><th scope="col">{t("commdoc.item")}</th><th scope="col">{t("commdoc.unit")}</th><th scope="col">{t("commdoc.quantity")}</th>{sealed && <th scope="col">{t("commdoc.line_net")}</th>}<th scope="col" /></tr></thead>
              <tbody>
                {form.lines.map((l, i) => (
                  <tr key={l.key} data-testid={`line-${i}`}>
                    <td>{i + 1}</td>
                    <td><ItemPicker id={`cd-item-${i}`} item={l.item} disabled={!canEdit} error={errors[`lines[${i}].itemId`]}
                      onPick={(item) => setForm((f) => ({ ...f, lines: f.lines.map((r) => (r.key === l.key ? { ...r, item, unitId: item.defaultUnitId } : r)) }))} /></td>
                    <td>
                      <Field id={`cd-unit-${i}`} label={t("commdoc.unit")} error={errors[`lines[${i}].unitId`]}>
                        {(aria) => <select {...aria} value={l.unitId} disabled={!canEdit || !l.item} onChange={(e) => setForm((f) => ({ ...f, lines: f.lines.map((r) => (r.key === l.key ? { ...r, unitId: e.target.value } : r)) }))}><option value="">{t("form.select")}</option>{(l.item?.units ?? []).map((u) => <option key={u.id} value={u.id}>{u.code}</option>)}</select>}
                      </Field>
                    </td>
                    <td>
                      <Field id={`cd-qty-${i}`} label={t("commdoc.quantity")} error={errors[`lines[${i}].quantity`]}>
                        {(aria) => <input {...aria} value={l.quantity} inputMode="decimal" dir="ltr" readOnly={!canEdit} onChange={(e) => setForm((f) => ({ ...f, lines: f.lines.map((r) => (r.key === l.key ? { ...r, quantity: e.target.value } : r)) }))} />}
                      </Field>
                    </td>
                    {sealed && <td className="num"><bdi dir="ltr" data-testid={`line-net-${i}`}>{money(doc!.lines[i]?.lineNet ?? null)}</bdi></td>}
                    <td>{canEdit && <button type="button" onClick={() => setForm((f) => ({ ...f, lines: f.lines.filter((r) => r.key !== l.key) }))}>{t("commdoc.remove_line")}</button>}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          {canEdit && <button type="button" onClick={() => setForm((f) => ({ ...f, lines: [...f.lines, { key: ++rowKey, item: null, unitId: "", quantity: "" }] }))}>{t("commdoc.add_line")}</button>}
        </div>
      ),
    },
    ...(doc ? [{
      id: "links", label: t("commdoc.tab.links"),
      content: <LinksPanel doc={doc} canLink={can("company.commercialdocs.update")} />,
    } as TabItem] : []),
    ...(doc ? [{
      id: "history", label: t("commdoc.tab.history"),
      content: (
        <div className="table-wrap">
          <table className="data-table" data-testid="history">
            <caption className="visually-hidden">{t("commdoc.tab.history")}</caption>
            <thead><tr><th scope="col">{t("commdoc.event")}</th><th scope="col">{t("commdoc.event_status")}</th><th scope="col">{t("commdoc.event_at")}</th><th scope="col">{t("commdoc.event_actor")}</th><th scope="col">{t("commdoc.event_reason")}</th></tr></thead>
            <tbody>{doc.events.map((e, i) => <tr key={`${e.revision}-${e.kind}-${i}`}><td>{t(`commdoc.event.${e.kind}`)}</td><td>{t(`commdoc.status.${e.toStatus}`)}</td><td><bdi dir="ltr">{e.atUtc.slice(0, 19).replace("T", " ")}</bdi></td><td><bdi dir="ltr">{shortId(e.actorUserId)}</bdi></td><td><bdi>{e.reason ?? ""}</bdi></td></tr>)}</tbody>
          </table>
        </div>
      ),
    } as TabItem] : []),
  ];
  const activeTab = tabs.some((x) => x.id === tab) ? tab : "document";

  return (
    <section aria-labelledby="page-title" data-testid="commercial-editor">
      <PageHeader title={doc ? (doc.officialNumber ?? t("commdoc.unnumbered")) : t("commdoc.new")}
        description={doc ? `${doc.docType} — ${doc.documentDate} — ${partyNames.get(doc.partyId) ?? ""}` : t("commdoc.new_subtitle")}
        actions={doc && <span className={statusChip(status)} data-testid="doc-status">{t(`commdoc.status.${status}`)}</span>} />
      <Banner kind="info" testId="no-effect-note">{t("commdoc.no_effect_note")}</Banner>
      {doc && status === 3 && canEdit && <Banner kind="info" testId="reapproval-note">{t("commdoc.reapproval_note")}</Banner>}
      {!canEdit && !isNew && <Banner kind="info" testId="form-read-only">{t("commdoc.read_only_doc")}</Banner>}
      {conflict && <Banner kind="error" testId="form-conflict">{t("commdoc.error.revision_conflict")} <button type="button" onClick={() => void load()}>{t("form.reload_latest")}</button></Banner>}
      {error && <Banner kind="error" testId="form-error">{error}</Banner>}
      <form onSubmit={save} noValidate>
        <Tabs label={t("commdoc.tabs_label")} tabs={tabs} value={activeTab} onChange={setTab} testId="commercial-tabs" />
        <div className="actions" data-testid="commercial-actions">
          <button type="button" onClick={onClose}>{t("inv.cancel")}</button>
          {canEdit && <button type="submit" className="primary" disabled={busy}>{busy ? t("loading") : t("inv.save")}</button>}
          {doc && status === 1 && can("company.commercialdocs.submit") && <button type="button" disabled={busy || dirty} title={dirty ? t("commdoc.save_first") : undefined} onClick={() => setConfirmSubmit(true)}>{t("commdoc.submit")}</button>}
          {doc && status === 2 && can("company.commercialdocs.submit") && <button type="button" disabled={busy} onClick={() => void act(() => auth.api.withdrawCommercialDocument(doc.id, rev), t("commdoc.withdrawn"))}>{t("commdoc.withdraw")}</button>}
          {doc && status === 2 && can("company.commercialdocs.approve") && <button type="button" className="primary" data-testid="approve" disabled={busy || mine} title={mine ? t("commdoc.self_approval") : undefined} onClick={() => void act(() => auth.api.approveCommercialDocument(doc.id, rev), t("commdoc.approved"))}>{t("commdoc.approve")}</button>}
          {doc && status === 2 && can("company.commercialdocs.approve") && <button type="button" data-testid="reject" disabled={busy || mine} onClick={() => { setReason(""); setReasonError(null); setDialog("reject"); }}>{t("commdoc.reject")}</button>}
          {doc && status !== 5 && can("company.commercialdocs.cancel") && <button type="button" className="danger" data-testid="cancel-document" disabled={busy} onClick={() => { setReason(""); setReasonError(null); setDialog("cancel"); }}>{t("commdoc.cancel_document")}</button>}
        </div>
        {doc && status === 2 && mine && can("company.commercialdocs.approve") && <p className="hint" data-testid="self-approval-note">{t("commdoc.self_approval")}</p>}
      </form>
      {confirmSubmit && doc && <ConfirmDialog title={t("commdoc.submit")} message={t("commdoc.submit_message")} confirmLabel={t("form.confirm")} onCancel={() => setConfirmSubmit(false)} onConfirm={() => void act(() => auth.api.submitCommercialDocument(doc.id, rev), t("commdoc.submitted"))} />}
      {dialog && doc && (
        <ConfirmDialog title={dialog === "reject" ? t("commdoc.reject") : t("commdoc.cancel_document")} message={dialog === "reject" ? t("commdoc.reject_message") : t("commdoc.cancel_message")} confirmLabel={t("form.confirm")}
          onCancel={() => setDialog(null)}
          onConfirm={() => { if (!reason.trim()) { setReasonError(t("commdoc.reason_required")); return; } setReasonError(null); void act(() => (dialog === "reject" ? auth.api.rejectCommercialDocument(doc.id, reason.trim(), rev) : auth.api.cancelCommercialDocument(doc.id, reason.trim(), rev)), t(dialog === "reject" ? "commdoc.rejected" : "commdoc.cancelled")); }}>
          <Field id="cd-reason" label={t("commdoc.reason")} error={reasonError ?? undefined} required>{(aria) => <textarea {...aria} value={reason} rows={3} maxLength={500} onChange={(e) => { setReason(e.target.value); setReasonError(null); }} />}</Field>
        </ConfirmDialog>
      )}
    </section>
  );
}

type Api = ReturnType<typeof useAuth>["api"];
/** The unit codes of an item come from the units lookup by id (one request, only the ids the item uses). */
async function toItemRef(api: Api, it: InventoryItem): Promise<ItemRef> {
  const ids = [...new Set([it.baseUnitId, it.salesUnitId, it.purchaseUnitId, ...it.alternateUnits.map((u) => u.unitId)].filter((x): x is string => !!x))];
  const codes = new Map((await api.lookupInventoryUnits(ids)).map((u) => [u.id, u.code]));
  return { id: it.id, code: it.code, nameAr: it.nameAr, nameEn: it.nameEn, units: ids.map((id) => ({ id, code: codes.get(id) ?? shortId(id) })), defaultUnitId: it.salesUnitId ?? it.baseUnitId };
}

/** Searches customers and suppliers on the server (the caller needs the party-role view permission, otherwise the lookup is refused and says so). */
function PartyPicker({ value, error, disabled, onPick }: { value: PartyRef | null; error?: string; disabled: boolean; onPick: (p: PartyRef) => void }) {
  const { t } = useI18n();
  const auth = useAuth();
  const [term, setTerm] = useState("");
  const [results, setResults] = useState<PartyRole[]>([]);
  const [denied, setDenied] = useState(false);
  useEffect(() => {
    if (!term.trim()) { setResults([]); return; }
    const h = setTimeout(() => {
      void auth.api.listPartyRoles(undefined, { search: term.trim(), limit: 20 }).then((r) => { setDenied(false); setResults(r.items.filter((x) => x.isActive)); })
        .catch((e) => { if (e instanceof ApiError && e.status === 403) setDenied(true); setResults([]); });
    }, 250);
    return () => clearTimeout(h);
  }, [term, auth.api]);
  return (
    <div>
      {value && <div data-testid="party-chosen"><bdi dir="ltr">{value.code}</bdi> — <bdi>{value.name}</bdi></div>}
      {!disabled && (
        <Field id="cd-party" label={t("commdoc.select_party")} error={error} required>
          {(aria) => <input {...aria} type="search" value={term} dir="auto" autoComplete="off" onChange={(e) => setTerm(e.target.value)} />}
        </Field>
      )}
      {denied && <p className="hint" data-testid="party-denied">{t("commdoc.party_lookup_denied")}</p>}
      {!disabled && results.length > 0 && (
        <select aria-label={t("commdoc.pick_party")} value="" onChange={(e) => { const r = results.find((x) => x.partyId === e.target.value); if (r) { onPick({ id: r.partyId, code: r.partyCode, name: r.partyName }); setTerm(""); setResults([]); } }}>
          <option value="">{t("form.select")}</option>
          {results.map((r) => <option key={r.id} value={r.partyId}>{r.partyCode} — {r.partyName}</option>)}
        </select>
      )}
      {disabled && error && <p className="field-error">{error}</p>}
    </div>
  );
}

/** Searches the items on the server (the caller needs the items view permission) and lets the user pick one; the unit codes come from the units lookup by id. */
function ItemPicker({ id, item, disabled, error, onPick }: { id: string; item: ItemRef | null; disabled: boolean; error?: string; onPick: (i: ItemRef) => void }) {
  const { t, locale } = useI18n();
  const auth = useAuth();
  const [term, setTerm] = useState("");
  const [results, setResults] = useState<InventoryItem[]>([]);
  const [denied, setDenied] = useState(false);
  useEffect(() => {
    if (!term.trim()) { setResults([]); return; }
    const h = setTimeout(() => {
      void auth.api.listInventoryItems({ search: term.trim(), active: true, limit: 20 }).then((r) => { setDenied(false); setResults(r.items); })
        .catch((e) => { if (e instanceof ApiError && e.status === 403) setDenied(true); setResults([]); });
    }, 250);
    return () => clearTimeout(h);
  }, [term, auth.api]);
  return (
    <div>
      {item && <div><bdi dir="ltr">{item.code}</bdi> — <bdi>{invName(item, locale)}</bdi></div>}
      {!disabled && (
        <Field id={id} label={t("commdoc.find_item")} error={error}>
          {(aria) => <input {...aria} type="search" value={term} dir="auto" autoComplete="off" onChange={(e) => setTerm(e.target.value)} />}
        </Field>
      )}
      {denied && <p className="hint" data-testid="item-denied">{t("commdoc.item_lookup_denied")}</p>}
      {!disabled && results.length > 0 && (
        <select aria-label={t("commdoc.pick_item")} value="" onChange={(e) => { const it = results.find((r) => r.id === e.target.value); if (it) { void toItemRef(auth.api, it).then(onPick); setTerm(""); setResults([]); } }}>
          <option value="">{t("form.select")}</option>
          {results.map((r) => <option key={r.id} value={r.id}>{r.code} — {invName(r, locale)}</option>)}
        </select>
      )}
      {disabled && error && <p className="field-error">{error}</p>}
    </div>
  );
}

const LINK_KINDS: DocumentLinkKind[] = [1, 2, 3, 4];
/** The server field of a refusal to the form field that shows it (the target document is this one, so its refusal is shown on the source). */
const LINK_FIELD: Record<string, string> = { fromDocumentId: "fromDocumentId", toDocumentId: "fromDocumentId", fromLineNo: "fromLineNo", toLineNo: "toLineNo", kind: "kind", quantity: "quantity", idempotencyKey: "quantity" };

/**
 * The Links tab (Phase 3.13 S2 API, S6 screen): the derived line quantities (ordered, fulfilled, invoiced, returned, computed by the server from the links), the links of this document in both directions and, for an APPROVED
 * document and a user who may update it, a form that links a line of another approved document (the SOURCE) to a line of THIS document (the TARGET: the server authorizes on the target's branch). The form never sends a
 * creator, a total or a price. One idempotency key belongs to one form attempt: a retry of the same submit replays the same link, and a new key starts after a success. Every refusal is translated; in production no
 * type pair is registered yet, so the server refuses with `link_not_allowed` and the empty state explains that links appear when linkable types are activated. Links are append-only: there is no remove button.
 */
function LinksPanel({ doc, canLink }: { doc: CommercialDocument; canLink: boolean }) {
  const { t } = useI18n();
  const auth = useAuth();
  const approved = doc.status === 3;
  const [links, setLinks] = useState<DocumentLink[] | null>(null);
  const [quantities, setQuantities] = useState<LineQuantity[]>([]);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [candidates, setCandidates] = useState<CommercialDocument[]>([]);
  const [sourceId, setSourceId] = useState("");
  const [source, setSource] = useState<CommercialDocument | null>(null);
  const [sourceLine, setSourceLine] = useState("");
  const [targetLine, setTargetLine] = useState("");
  const [kind, setKind] = useState<DocumentLinkKind>(1);
  const [quantity, setQuantity] = useState("");
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [key, setKey] = useState(newKey);

  const reload = useCallback(async () => {
    if (!approved) { setLinks([]); return; }
    try {
      const [l, q] = await Promise.all([auth.api.listDocumentLinks(doc.id), auth.api.getLineQuantities(doc.id)]);
      setLinks(l); setQuantities(q); setLoadError(null);
    } catch (e) { if (!auth.handleError(e)) setLoadError(errorText(t, e, "commdoc.links.error.")); setLinks([]); }
  }, [auth.api, auth.handleError, doc.id, approved, t]);
  useEffect(() => { void reload(); }, [reload]);

  // the candidate sources: approved documents of the same branch, party and currency (the server checks the same rules again)
  useEffect(() => {
    if (!approved || !canLink) return;
    void auth.api.listCommercialDocuments({ status: 3, branchId: doc.branchId, limit: 100 }).then((list) => setCandidates(list.filter((d) => d.id !== doc.id && d.partyId === doc.partyId && d.currency === doc.currency))).catch(() => setCandidates([]));
  }, [auth.api, approved, canLink, doc.id, doc.branchId, doc.partyId, doc.currency]);

  async function chooseSource(id: string) {
    setSourceId(id); setSource(null); setSourceLine(""); setErrors({}); setError(null);
    if (!id) return;
    try { setSource(await auth.api.getCommercialDocument(id)); } catch (e) { if (!auth.handleError(e)) setError(errorText(t, e, "commdoc.links.error.")); }
  }

  async function create() {
    const v: Record<string, string> = {};
    if (!sourceId) v.fromDocumentId = t("commdoc.links.field.required");
    if (!sourceLine) v.fromLineNo = t("commdoc.links.field.required");
    if (!targetLine) v.toLineNo = t("commdoc.links.field.required");
    if (Number.isNaN(parsePositiveDecimal(quantity))) v.quantity = t("commdoc.field.invalid_quantity");
    setErrors(v);
    if (Object.keys(v).length > 0) { setError(t("commdoc.links.fix")); return; }
    setBusy(true); setError(null); setNotice(null);
    try {
      await auth.api.createDocumentLink({ fromDocumentId: sourceId, fromLineNo: Number(sourceLine), toDocumentId: doc.id, toLineNo: Number(targetLine), kind, quantity: parsePositiveDecimal(quantity), idempotencyKey: key });
      setNotice(t("commdoc.links.created")); setKey(newKey()); setQuantity(""); setErrors({});
      await reload();
    } catch (err) {
      if (auth.handleError(err)) return;
      if (err instanceof ApiError && err.status === 400) {
        const out: Record<string, string> = {};
        for (const f of err.details.fields ?? []) { const k = `commdoc.links.field.${f.code}`; out[LINK_FIELD[f.field] ?? "quantity"] = t(k) === k ? t("field.invalid") : t(k); }
        setErrors(out); setError(t("commdoc.links.fix"));
      } else setError(errorText(t, err, "commdoc.links.error."));
    } finally { setBusy(false); }
  }

  const lineOf = (d: CommercialDocument | null, no: string) => d?.lines.find((l) => String(l.lineNo) === no);
  const clean = (e: Record<string, string>, k: string) => (e[k] ? e[k] : undefined);
  const otherLabel = (l: DocumentLink) => { const id = l.toDocumentId === doc.id ? l.fromDocumentId : l.toDocumentId; return candidates.find((c) => c.id === id)?.officialNumber ?? shortId(id); };
  return (
    <div data-testid="links-panel">
      <p className="hint">{t("commdoc.links.intro")}</p>
      {loadError && <Banner kind="error" testId="links-load-error">{loadError}</Banner>}
      {!approved && <Banner kind="info" testId="links-not-approved">{t("commdoc.links.not_approved")}</Banner>}
      {approved && (
        <Card title={t("commdoc.links.quantities")}>
          <div className="table-wrap">
            <table className="data-table" data-testid="line-quantities">
              <caption className="visually-hidden">{t("commdoc.links.quantities")}</caption>
              <thead><tr><th scope="col">#</th><th scope="col">{t("commdoc.links.ordered")}</th><th scope="col">{t("commdoc.links.fulfilled")}</th><th scope="col">{t("commdoc.links.invoiced")}</th><th scope="col">{t("commdoc.links.returned")}</th><th scope="col">{t("commdoc.links.remaining")}</th></tr></thead>
              <tbody>{quantities.map((q) => <tr key={q.lineNo} data-testid={`qty-${q.lineNo}`}><td>{q.lineNo}</td><td className="num"><bdi dir="ltr">{q.ordered}</bdi></td><td className="num"><bdi dir="ltr">{q.fulfilled}</bdi></td><td className="num"><bdi dir="ltr">{q.invoiced}</bdi></td><td className="num"><bdi dir="ltr">{q.returned}</bdi></td><td className="num"><bdi dir="ltr">{q.remainingToFulfil}</bdi></td></tr>)}</tbody>
            </table>
          </div>
        </Card>
      )}
      <Card title={t("commdoc.links.list")}>
        <div className="table-wrap">
          <table className="data-table" data-testid="links-table">
            <caption className="visually-hidden">{t("commdoc.links.list")}</caption>
            <thead><tr><th scope="col">{t("commdoc.links.direction")}</th><th scope="col">{t("commdoc.links.other")}</th><th scope="col">{t("commdoc.links.line")}</th><th scope="col">{t("commdoc.links.kind")}</th><th scope="col">{t("commdoc.links.quantity")}</th><th scope="col">{t("commdoc.links.at")}</th></tr></thead>
            <tbody>
              {links !== null && links.length === 0 && <tr><td colSpan={6} data-testid="links-empty">{t("commdoc.links.none")} {t("commdoc.links.none_hint")}</td></tr>}
              {(links ?? []).map((l) => {
                const outgoing = l.fromDocumentId === doc.id;
                return (
                  <tr key={l.id} data-testid={`link-${l.id}`}>
                    <td>{outgoing ? t("commdoc.links.out") : t("commdoc.links.in")}</td><td><bdi dir="ltr">{otherLabel(l)}</bdi></td>
                    <td><bdi dir="ltr">{outgoing ? `${l.fromLineNo} → ${l.toLineNo}` : `${l.toLineNo} ← ${l.fromLineNo}`}</bdi></td><td>{t(`commdoc.links.kind.${l.kind}`)}</td>
                    <td className="num"><bdi dir="ltr">{l.quantity}</bdi></td><td><bdi dir="ltr">{l.createdAtUtc.slice(0, 19).replace("T", " ")}</bdi></td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      </Card>
      {approved && canLink && (
        <Card title={t("commdoc.links.create")} hint={t("commdoc.links.create_hint")}>
          {notice && <Banner kind="success" testId="link-created">{notice}</Banner>}
          {error && <Banner kind="error" testId="link-error">{error}</Banner>}
          {candidates.length === 0 && <p className="hint" data-testid="no-sources">{t("commdoc.links.source_none")}</p>}
          <div className="fields">
            <Field id="cl-source" label={t("commdoc.links.source")} error={clean(errors, "fromDocumentId")} required>
              {(aria) => <select {...aria} value={sourceId} onChange={(e) => void chooseSource(e.target.value)}><option value="">{t("form.select")}</option>{candidates.map((c) => <option key={c.id} value={c.id}>{c.officialNumber ?? shortId(c.id)} — {c.docType}</option>)}</select>}
            </Field>
            <Field id="cl-source-line" label={t("commdoc.links.source_line")} error={clean(errors, "fromLineNo")} required>
              {(aria) => <select {...aria} value={sourceLine} disabled={!source} onChange={(e) => setSourceLine(e.target.value)}><option value="">{t("form.select")}</option>{(source?.lines ?? []).map((l) => <option key={l.lineNo} value={l.lineNo}>#{l.lineNo} — {l.quantity}</option>)}</select>}
            </Field>
            <Field id="cl-target-line" label={t("commdoc.links.target_line")} error={clean(errors, "toLineNo")} required>
              {(aria) => <select {...aria} value={targetLine} onChange={(e) => setTargetLine(e.target.value)}><option value="">{t("form.select")}</option>{doc.lines.map((l) => <option key={l.lineNo} value={l.lineNo}>#{l.lineNo} — {l.quantity}</option>)}</select>}
            </Field>
            <Field id="cl-kind" label={t("commdoc.links.kind")} error={clean(errors, "kind")} required>
              {(aria) => <select {...aria} value={kind} onChange={(e) => setKind(Number(e.target.value) as DocumentLinkKind)}>{LINK_KINDS.map((k) => <option key={k} value={k}>{t(`commdoc.links.kind.${k}`)}</option>)}</select>}
            </Field>
            <Field id="cl-quantity" label={t("commdoc.links.quantity")} error={clean(errors, "quantity")} required>
              {(aria) => <input {...aria} value={quantity} inputMode="decimal" dir="ltr" autoComplete="off" onChange={(e) => setQuantity(e.target.value)} onKeyDown={(e) => { if (e.key === "Enter") e.preventDefault(); }} />}
            </Field>
          </div>
          {lineOf(source, sourceLine) && <p className="hint" data-testid="source-line-hint">{t("commdoc.links.ordered")}: <bdi dir="ltr">{lineOf(source, sourceLine)!.quantity}</bdi></p>}
          <div className="actions"><button type="button" className="primary" data-testid="create-link" disabled={busy} onClick={() => void create()}>{busy ? t("loading") : t("commdoc.links.submit")}</button></div>
        </Card>
      )}
    </div>
  );
}
