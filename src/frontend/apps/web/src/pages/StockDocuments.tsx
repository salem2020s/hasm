import {
  ApiError, type StockDocType, type StockDocument, type StockPosting, type StockDocumentCapabilities, type StockDocumentInput, type StockDocumentSummary, type StockItemRef, type StockWarehouseRef,
} from "@hasm/api-client";
import { Tabs, useI18n, type TabItem } from "@hasm/ui";
import { useCallback, useEffect, useMemo, useRef, useState, type FormEvent } from "react";
import { useAuth } from "../auth";
import { Banner, Card, ConfirmDialog, errorText, Field, Loading, PageHeader } from "../components";
import { useReportUnsaved, useUnsaved } from "../unsaved";
import { blankToNull, invName } from "./inventoryCommon";

/**
 * Stock movement documents (Phase 3.5): receipt, issue and warehouse-to-warehouse transfer, from draft to approval. ONE implementation for Web and Windows Desktop. The screen only drives the server: every rule
 * (warehouse-scoped permissions, separation of duties, validation against the real items, units and warehouses, exact unit conversion, numbering, fiscal period, life cycle, audit) is enforced by the server and
 * PostgreSQL. These documents change NO stock quantity: nothing is posted, no balance exists and no accounting entry is created; a later slice posts approved documents. The editor uses the shared Tabs standard
 * (General, Lines, History); the document identity and the workflow actions stay visible on every tab and switching tabs never saves or resets input.
 */
const PAGE = 25;
const TYPES: StockDocType[] = [1, 2, 3];
/** The user's LOCAL calendar date (not the UTC date: near midnight the UTC date is another day and could pick the wrong fiscal period). */
const todayIso = () => { const d = new Date(); const p = (n: number) => String(n).padStart(2, "0"); return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}`; };
/** Amounts are shown with at least two decimals (exact, no floating point artefacts); the server enforces the scale of the base currency. */
const money = (n: number) => n.toLocaleString("en-US", { minimumFractionDigits: 2, maximumFractionDigits: 6, useGrouping: false });

interface LineRow { key: number; item: StockItemRef | null; unitId: string; quantity: string; notes: string }
interface Form { docType: StockDocType; source: string; target: string; date: string; reference: string; notes: string; lines: LineRow[] }
let rowKey = 0;
const snapshot = (f: Form) => JSON.stringify({ ...f, lines: f.lines.map((l) => [l.item?.id ?? "", l.unitId, l.quantity, l.notes]) });
const trimNumber = (n: number) => String(Number(n.toFixed(10)));

type Editor = { mode: "new"; docType: StockDocType } | { mode: "edit"; id: string };

export const whName = (w: Pick<StockWarehouseRef, "code" | "nameAr" | "nameEn"> | undefined, locale: string) => (w ? `${w.code} — ${invName(w, locale)}` : "—");

export function StockDocumentsPage() {
  const { t, locale } = useI18n();
  const tRef = useRef(t);
  tRef.current = t;
  const auth = useAuth();
  const { guard } = useUnsaved();
  const [caps, setCaps] = useState<StockDocumentCapabilities | null>(null);
  const [warehouses, setWarehouses] = useState<StockWarehouseRef[] | null>(null);
  const [docs, setDocs] = useState<StockDocumentSummary[] | null>(null);
  const [total, setTotal] = useState(0);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [type, setType] = useState("");
  const [status, setStatus] = useState("");
  const [warehouse, setWarehouse] = useState("");
  const [from, setFrom] = useState("");
  const [to, setTo] = useState("");
  const [search, setSearch] = useState("");
  const [offset, setOffset] = useState(0);
  const [editor, setEditor] = useState<Editor | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  const loadRefs = useCallback(async () => {
    try {
      const [c, w] = await Promise.all([auth.api.getStockDocumentCapabilities(), auth.api.listStockWarehouses()]);
      setCaps(c); setWarehouses(w.warehouses);
    } catch (e) { if (!auth.handleError(e)) setLoadError(errorText(tRef.current, e, "stock.error.")); }
  }, [auth.api, auth.handleError]);
  useEffect(() => { void loadRefs(); }, [loadRefs]);

  const loadDocs = useCallback(async () => {
    try {
      const page = await auth.api.listStockDocuments({
        type: type ? (Number(type) as StockDocType) : undefined, status: status ? (Number(status) as 1) : undefined, warehouseId: warehouse || undefined, from: from || undefined, to: to || undefined,
        search: search.trim() || undefined, offset, limit: PAGE,
      });
      setDocs(page.documents); setTotal(page.total); setLoadError(null);
    } catch (e) { if (!auth.handleError(e)) setLoadError(errorText(tRef.current, e, "stock.error.")); }
  }, [auth.api, auth.handleError, type, status, warehouse, from, to, search, offset]);
  useEffect(() => { const h = setTimeout(() => void loadDocs(), 250); return () => clearTimeout(h); }, [loadDocs]);

  const whById = useMemo(() => new Map((warehouses ?? []).map((w) => [w.id, w])), [warehouses]);
  if (loadError) return <Banner kind="error">{loadError}</Banner>;
  if (!caps || !warehouses || !docs) return <Loading />;

  if (editor) {
    return (
      <DocumentEditor key={editor.mode === "edit" ? editor.id : `new-${editor.docType}`} editor={editor} caps={caps} warehouses={warehouses}
        onClose={() => guard(() => { setEditor(null); void loadDocs(); })} onChanged={(m) => { setNotice(m); void loadDocs(); }} onSwitch={(id) => setEditor({ mode: "edit", id })} />
    );
  }
  const canCreate = caps.create.length > 0;
  const where = (d: StockDocumentSummary) => d.docType === 3 ? `${whName(whById.get(d.sourceWarehouseId!), locale)} ← ${whName(whById.get(d.targetWarehouseId!), locale)}` : whName(whById.get((d.sourceWarehouseId ?? d.targetWarehouseId)!), locale);

  return (
    <section aria-labelledby="page-title">
      <PageHeader title={t("stock.title")} description={t("stock.subtitle")}
        actions={canCreate && <>{TYPES.map((x) => <button key={x} type="button" className={x === 1 ? "primary" : undefined} disabled={x === 3 && caps.create.length < 2} onClick={() => guard(() => { setNotice(null); setEditor({ mode: "new", docType: x }); })}>{t(`stock.new.${x}`)}</button>)}</>} />
      <Banner kind="info" testId="no-posting-note">{t("stock.no_posting_note")}</Banner>
      {!canCreate && <Banner kind="info" testId="read-only">{t("form.read_only")}</Banner>}
      {notice && <Banner kind="success" testId="saved">{notice}</Banner>}
      <Card title={t("stock.list")}>
        <div className="toolbar">
          <Field id="sd-search" label={t("stock.search")} hint={t("stock.search_hint")}>{(aria) => <input {...aria} type="search" value={search} onChange={(e) => { setOffset(0); setSearch(e.target.value); }} autoComplete="off" />}</Field>
          <Field id="sd-type" label={t("stock.type")}>
            {(aria) => <select {...aria} value={type} onChange={(e) => { setOffset(0); setType(e.target.value); }}><option value="">{t("inv.filter_all")}</option>{TYPES.map((x) => <option key={x} value={x}>{t(`stock.type.${x}`)}</option>)}</select>}
          </Field>
          <Field id="sd-status" label={t("stock.status")}>
            {(aria) => <select {...aria} value={status} onChange={(e) => { setOffset(0); setStatus(e.target.value); }}><option value="">{t("inv.filter_all")}</option>{[1, 2, 3, 4, 5].map((x) => <option key={x} value={x}>{t(`stock.status.${x}`)}</option>)}</select>}
          </Field>
          <Field id="sd-warehouse" label={t("stock.warehouse")}>
            {(aria) => <select {...aria} value={warehouse} onChange={(e) => { setOffset(0); setWarehouse(e.target.value); }}><option value="">{t("inv.filter_all")}</option>{warehouses.map((w) => <option key={w.id} value={w.id}>{whName(w, locale)}</option>)}</select>}
          </Field>
          <Field id="sd-from" label={t("stock.from")}>{(aria) => <input {...aria} type="date" value={from} onChange={(e) => { setOffset(0); setFrom(e.target.value); }} dir="ltr" />}</Field>
          <Field id="sd-to" label={t("stock.to")}>{(aria) => <input {...aria} type="date" value={to} onChange={(e) => { setOffset(0); setTo(e.target.value); }} dir="ltr" />}</Field>
        </div>
        <div className="table-wrap">
          <table className="data-table">
            <caption className="visually-hidden">{t("stock.list")}</caption>
            <thead><tr><th scope="col">{t("stock.number")}</th><th scope="col">{t("stock.type")}</th><th scope="col">{t("stock.date")}</th><th scope="col">{t("stock.warehouse")}</th><th scope="col">{t("stock.reference")}</th><th scope="col">{t("stock.lines_count")}</th><th scope="col">{t("stock.status")}</th><th scope="col">{t("inv.actions")}</th></tr></thead>
            <tbody>
              {docs.length === 0 && <tr><td colSpan={8}>{total === 0 && !search && !type && !status && !warehouse && !from && !to ? t("stock.none") : t("stock.none_filtered")}</td></tr>}
              {docs.map((d) => (
                <tr key={d.id} data-testid={`doc-${d.id}`}>
                  <td><bdi dir="ltr">{d.number ?? t("stock.unnumbered")}</bdi></td><td>{t(`stock.type.${d.docType}`)}</td><td><bdi dir="ltr">{d.documentDate}</bdi></td><td><bdi>{where(d)}</bdi></td>
                  <td><bdi>{d.reference ?? ""}</bdi></td><td className="num">{d.lineCount}</td>
                  <td><span className={`chip ${d.status === 3 ? "chip-ok" : d.status >= 4 ? "chip-off" : ""}`}>{t(`stock.status.${d.status}`)}</span></td>
                  <td><button type="button" onClick={() => guard(() => { setNotice(null); setEditor({ mode: "edit", id: d.id }); })}>{t("inv.view")}</button></td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
        <div className="toolbar" data-testid="paging">
          <button type="button" disabled={offset === 0} onClick={() => setOffset(Math.max(0, offset - PAGE))}>{t("inv.items.previous")}</button>
          <span aria-live="polite">{t("inv.items.paging", { from: String(total === 0 ? 0 : offset + 1), to: String(offset + docs.length), total: String(total) })}</span>
          <button type="button" disabled={offset + PAGE >= total} onClick={() => setOffset(offset + PAGE)}>{t("inv.items.next")}</button>
        </div>
      </Card>
    </section>
  );
}

const emptyForm = (docType: StockDocType): Form => ({ docType, source: "", target: "", date: todayIso(), reference: "", notes: "", lines: [{ key: ++rowKey, item: null, unitId: "", quantity: "", notes: "" }] });
const fromDoc = (d: StockDocument, items: Map<string, StockItemRef>): Form => ({
  docType: d.docType, source: d.sourceWarehouseId ?? "", target: d.targetWarehouseId ?? "", date: d.documentDate, reference: d.reference ?? "", notes: d.notes ?? "",
  lines: d.lines.map((l) => ({ key: ++rowKey, item: items.get(l.itemId) ?? { id: l.itemId, code: l.itemCode, nameAr: l.itemNameAr, nameEn: l.itemNameEn, units: [{ id: l.unitId, code: l.unitCode, nameAr: null, nameEn: null, decimals: 10, factor: l.factor, isBase: l.factor === 1 }] }, unitId: l.unitId, quantity: String(l.quantity), notes: l.notes ?? "" })),
});
const TAB_OF = (field: string): "general" | "lines" => (field.startsWith("lines") ? "lines" : "general");

function DocumentEditor({ editor, caps, warehouses, onClose, onChanged, onSwitch }: {
  editor: Editor; caps: StockDocumentCapabilities; warehouses: StockWarehouseRef[]; onClose: () => void; onChanged: (m: string) => void; onSwitch: (id: string) => void;
}) {
  const { t, locale } = useI18n();
  const auth = useAuth();
  const isNew = editor.mode === "new";
  const [doc, setDoc] = useState<StockDocument | null>(null);
  const [loading, setLoading] = useState(!isNew);
  const [form, setForm] = useState<Form>(() => emptyForm(editor.mode === "new" ? editor.docType : 1));
  const [base, setBase] = useState(() => snapshot(form));
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [error, setError] = useState<string | null>(null);
  const [conflict, setConflict] = useState(false);
  const [busy, setBusy] = useState(false);
  const [tab, setTab] = useState("general");
  const [dialog, setDialog] = useState<null | "reject" | "cancel" | "post" | "reverse">(null);
  const [posting, setPosting] = useState<StockPosting | null>(null);
  const [amounts, setAmounts] = useState<Record<number, string>>({});
  const [reverseDate, setReverseDate] = useState(todayIso());
  const [reason, setReason] = useState("");
  const [confirmSubmit, setConfirmSubmit] = useState(false);

  const load = useCallback(async () => {
    if (editor.mode !== "edit") return;
    try {
      const d = await auth.api.getStockDocument(editor.id);
      const refs = new Map<string, StockItemRef>();
      for (const code of new Set(d.lines.map((l) => l.itemCode))) for (const it of (await auth.api.searchStockItems(code).catch(() => ({ items: [] as StockItemRef[] }))).items) refs.set(it.id, it);
      const f = fromDoc(d, refs);
      let p: StockPosting | null = null;
      if (d.status === 3) { try { p = await auth.api.getStockPosting(d.id); } catch (err) { if (auth.handleError(err)) return; p = null; } }
      setPosting(p);
      setDoc(d); setForm(f); setBase(snapshot(f)); setErrors({}); setConflict(false); setLoading(false);
    } catch (e) { if (!auth.handleError(e)) { setError(errorText(t, e, "stock.error.")); setLoading(false); } }
  }, [auth.api, auth.handleError, editor, t]);
  useEffect(() => { void load(); }, [load]);

  const dirty = snapshot(form) !== base;
  useReportUnsaved("stock-document-form", dirty, t("stock.form_section"));
  const status = doc?.status ?? 1;
  const docInvolved = doc ? [doc.sourceWarehouseId, doc.targetWarehouseId].filter((x): x is string => !!x) : [];
  const allDoc = (ids: string[]) => docInvolved.length > 0 && docInvolved.every((w) => ids.includes(w));
  const canEdit = isNew ? caps.create.length > 0 : status === 1 && allDoc(caps.update);
  const mine = doc?.submittedBy === auth.user?.id;
  const set = <K extends keyof Form>(k: K, v: Form[K]) => setForm((f) => ({ ...f, [k]: v }));
  const choices = (ids: string[]) => warehouses.filter((w) => w.isActive && ids.includes(w.id));

  function validate(): Record<string, string> {
    const v: Record<string, string> = {};
    if (form.docType !== 1 && !form.source) v.sourceWarehouseId = t("field.required");
    if (form.docType !== 2 && !form.target) v.targetWarehouseId = t("field.required");
    if (form.docType === 3 && form.source && form.source === form.target) v.targetWarehouseId = t("stock.field.same_warehouse");
    if (!form.date) v.documentDate = t("field.required");
    if (form.lines.length === 0) v.lines = t("stock.field.lines_required");
    form.lines.forEach((l, i) => {
      if (!l.item) v[`lines[${i}].itemId`] = t("field.required");
      if (!l.unitId) v[`lines[${i}].unitId`] = t("field.required");
      const q = Number(l.quantity);
      if (!l.quantity.trim() || !Number.isFinite(q) || q <= 0 || !/^\d+(\.\d{1,10})?$/.test(l.quantity.trim())) v[`lines[${i}].quantity`] = t("stock.field.invalid_quantity");
    });
    return v;
  }
  const tabsWithErrors = (e: Record<string, string>) => new Set(Object.keys(e).map(TAB_OF));
  const toInput = (): StockDocumentInput => ({
    docType: form.docType, sourceWarehouseId: form.source || null, targetWarehouseId: form.target || null, documentDate: form.date, reference: blankToNull(form.reference), notes: blankToNull(form.notes),
    lines: form.lines.map((l) => ({ itemId: l.item!.id, unitId: l.unitId, quantity: Number(l.quantity), notes: blankToNull(l.notes) })),
  });

  function showServerError(err: unknown, onPosting = false) {
    if (auth.handleError(err)) return;
    if (err instanceof ApiError && err.status === 412) { setConflict(true); return; }
    if (err instanceof ApiError && err.status === 400) {
      const out: Record<string, string> = {};
      for (const f of err.details.fields ?? []) out[f.field] = t(`stock.field.${f.code}`) === `stock.field.${f.code}` ? (t(`field.${f.code}`) === `field.${f.code}` ? t("field.invalid") : t(`field.${f.code}`)) : t(`stock.field.${f.code}`);
      setErrors(out); setError(t("form.fix_errors"));
      if (onPosting) { setTab("posting"); return; }
      const first = (["general", "lines"] as const).find((id) => tabsWithErrors(out).has(id));
      if (first) setTab(first);
      return;
    }
    setError(errorText(t, err, "stock.error."));
  }

  async function save(e: FormEvent) {
    e.preventDefault();
    if (!canEdit) return;
    const v = validate();
    setErrors(v);
    if (Object.keys(v).length > 0) { setError(t("form.fix_errors")); const first = (["general", "lines"] as const).find((id) => tabsWithErrors(v).has(id)); if (first) setTab(first); return; }
    setBusy(true); setError(null); setConflict(false);
    try {
      if (editor.mode === "new") { const d = await auth.api.createStockDocument(toInput()); onChanged(t("stock.created")); setBase(snapshot(form)); onSwitch(d.id); }
      else { const d = await auth.api.updateStockDocument(editor.id, toInput(), doc!.revision); setDoc(d); setBase(snapshot(form)); onChanged(t("stock.saved")); await load(); }
    } catch (err) { showServerError(err); } finally { setBusy(false); }
  }

  async function act(run: () => Promise<unknown>, message: string, onPosting = false) {
    setBusy(true); setError(null); setConflict(false); setDialog(null); setConfirmSubmit(false);
    try { await run(); onChanged(message); await load(); setReason(""); }
    catch (err) { showServerError(err, onPosting); } finally { setBusy(false); }
  }

  const isReceipt = doc?.docType === 1;
  const AMOUNT = /^\d+(\.\d{1,6})?$/;
  /** The receipt amounts are validated here for a fast answer; the server validates them again (scale of the base currency, range). */
  function openPost() {
    if (!doc) return;
    if (isReceipt) {
      const v: Record<string, string> = {};
      if (doc.lines.some((l) => !AMOUNT.test((amounts[l.lineNo] ?? "").trim()))) v.lines = t("stock.field.amount_required_for_every_line");
      if (v.lines) { setErrors(v); setTab("posting"); return; }
    }
    setErrors({}); setError(null); setDialog("post");
  }
  const postLines = () => (isReceipt && doc ? doc.lines.map((l) => ({ lineNo: l.lineNo, amount: Number(amounts[l.lineNo]) })) : undefined);
  const previewTotal = isReceipt && doc ? doc.lines.reduce((sum, l) => sum + (AMOUNT.test((amounts[l.lineNo] ?? "").trim()) ? Number(amounts[l.lineNo]) : 0), 0) : 0;
  const canPost = !!doc && status === 3 && !posting && allDoc(caps.post);
  const canReverse = !!doc && !!posting && !posting.reversal && allDoc(caps.reverse);

  if (loading) return <Loading />;
  const invalid = tabsWithErrors(errors);
  const unitOptions = (item: StockItemRef | null) => item?.units ?? [];
  const tabs: TabItem[] = [
    {
      id: "general", label: t("stock.tab.general"), invalid: invalid.has("general"),
      content: (
        <div className="fields">
          <Field id="sd-doctype" label={t("stock.type")}>{(aria) => <input {...aria} readOnly value={t(`stock.type.${form.docType}`)} />}</Field>
          {form.docType !== 1 && (
            <Field id="sd-source" label={t("stock.source")} error={errors.sourceWarehouseId} required>
              {(aria) => <select {...aria} value={form.source} disabled={!canEdit} onChange={(e) => set("source", e.target.value)}><option value="">{t("form.select")}</option>{choices(isNew ? caps.create : caps.update).concat(warehouses.filter((w) => w.id === form.source && !caps.update.includes(w.id) && !caps.create.includes(w.id))).map((w) => <option key={w.id} value={w.id}>{whName(w, locale)}</option>)}</select>}
            </Field>
          )}
          {form.docType !== 2 && (
            <Field id="sd-target" label={t("stock.target")} error={errors.targetWarehouseId} required>
              {(aria) => <select {...aria} value={form.target} disabled={!canEdit} onChange={(e) => set("target", e.target.value)}><option value="">{t("form.select")}</option>{choices(isNew ? caps.create : caps.update).concat(warehouses.filter((w) => w.id === form.target && !caps.update.includes(w.id) && !caps.create.includes(w.id))).map((w) => <option key={w.id} value={w.id}>{whName(w, locale)}</option>)}</select>}
            </Field>
          )}
          <Field id="sd-date" label={t("stock.date")} error={errors.documentDate} required hint={t("stock.date_hint")}>{(aria) => <input {...aria} type="date" value={form.date} readOnly={!canEdit} onChange={(e) => set("date", e.target.value)} dir="ltr" />}</Field>
          <Field id="sd-reference" label={t("stock.reference")} error={errors.reference}>{(aria) => <input {...aria} value={form.reference} readOnly={!canEdit} maxLength={100} onChange={(e) => set("reference", e.target.value)} autoComplete="off" />}</Field>
          <Field id="sd-notes" label={t("stock.notes")} error={errors.notes}>{(aria) => <textarea {...aria} value={form.notes} readOnly={!canEdit} rows={3} maxLength={1000} onChange={(e) => set("notes", e.target.value)} />}</Field>
          {doc && (doc.decisionNote || doc.cancelReason) && <Field id="sd-decision" label={doc.cancelReason ? t("stock.cancel_reason") : t("stock.decision_note")}>{(aria) => <input {...aria} readOnly value={doc.cancelReason ?? doc.decisionNote ?? ""} />}</Field>}
        </div>
      ),
    },
    {
      id: "lines", label: t("stock.tab.lines"), invalid: invalid.has("lines"),
      content: (
        <div>
          {errors.lines && <Banner kind="error">{errors.lines}</Banner>}
          <p className="hint">{t("stock.lines_hint")}</p>
          <div className="table-wrap">
            <table className="data-table">
              <caption className="visually-hidden">{t("stock.tab.lines")}</caption>
              <thead><tr><th scope="col">#</th><th scope="col">{t("stock.item")}</th><th scope="col">{t("stock.unit")}</th><th scope="col">{t("stock.quantity")}</th><th scope="col">{t("stock.base_quantity")}</th><th scope="col">{t("stock.line_notes")}</th><th scope="col">{t("inv.actions")}</th></tr></thead>
              <tbody>
                {form.lines.map((l, i) => {
                  const unit = unitOptions(l.item).find((u) => u.id === l.unitId);
                  const q = Number(l.quantity);
                  const preview = unit && Number.isFinite(q) && q > 0 ? `${trimNumber(q * unit.factor)} ${unitOptions(l.item).find((u) => u.isBase)?.code ?? ""}` : "—";
                  return (
                    <tr key={l.key} data-testid={`line-${i}`}>
                      <td>{i + 1}</td>
                      <td>
                        <ItemPicker id={`sd-item-${i}`} item={l.item} disabled={!canEdit} error={errors[`lines[${i}].itemId`]}
                          onPick={(item) => setForm((f) => ({ ...f, lines: f.lines.map((r) => (r.key === l.key ? { ...r, item, unitId: item.units.find((u) => u.isBase)?.id ?? item.units[0]?.id ?? "" } : r)) }))} />
                      </td>
                      <td>
                        <Field id={`sd-unit-${i}`} label={t("stock.unit")} error={errors[`lines[${i}].unitId`]}>
                          {(aria) => <select {...aria} value={l.unitId} disabled={!canEdit || !l.item} onChange={(e) => setForm((f) => ({ ...f, lines: f.lines.map((r) => (r.key === l.key ? { ...r, unitId: e.target.value } : r)) }))}><option value="">{t("form.select")}</option>{unitOptions(l.item).map((u) => <option key={u.id} value={u.id}>{u.code}{u.isBase ? "" : ` (×${u.factor})`}</option>)}</select>}
                        </Field>
                      </td>
                      <td>
                        <Field id={`sd-qty-${i}`} label={t("stock.quantity")} error={errors[`lines[${i}].quantity`]}>
                          {(aria) => <input {...aria} value={l.quantity} inputMode="decimal" dir="ltr" readOnly={!canEdit} onChange={(e) => setForm((f) => ({ ...f, lines: f.lines.map((r) => (r.key === l.key ? { ...r, quantity: e.target.value } : r)) }))} />}
                        </Field>
                      </td>
                      <td><bdi dir="ltr" data-testid={`base-${i}`}>{doc && !dirty && doc.lines[i] ? `${doc.lines[i]!.baseQuantity} ${doc.lines[i]!.baseUnitCode}` : preview}</bdi></td>
                      <td><input aria-label={t("stock.line_notes")} value={l.notes} readOnly={!canEdit} maxLength={200} onChange={(e) => setForm((f) => ({ ...f, lines: f.lines.map((r) => (r.key === l.key ? { ...r, notes: e.target.value } : r)) }))} /></td>
                      <td>{canEdit && <button type="button" onClick={() => setForm((f) => ({ ...f, lines: f.lines.filter((r) => r.key !== l.key) }))}>{t("inv.item.remove")}</button>}</td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
          {canEdit && <button type="button" onClick={() => setForm((f) => ({ ...f, lines: [...f.lines, { key: ++rowKey, item: null, unitId: "", quantity: "", notes: "" }] }))}>{t("stock.add_line")}</button>}
        </div>
      ),
    },
    ...(doc && status === 3 ? [{
      id: "posting", label: t("stock.tab.posting"), invalid: Object.keys(errors).length > 0,
      content: (
        <div data-testid="posting-panel">
          {Object.keys(errors).length > 0 && <Banner kind="error" testId="posting-error">{[...new Set(Object.values(errors))].join(" ")}</Banner>}
          {!posting ? (
            <div>
              <Banner kind="info" testId="not-executed">{t("stock.posting.not_executed")}</Banner>
              {isReceipt ? (
                <div>
                  <p className="hint">{t("stock.posting.amount_hint")}</p>
                  <div className="table-wrap">
                    <table className="data-table">
                      <caption className="visually-hidden">{t("stock.posting.lines")}</caption>
                      <thead><tr><th scope="col">#</th><th scope="col">{t("stock.item")}</th><th scope="col">{t("stock.base_quantity")}</th><th scope="col">{t("stock.posting.amount")}</th></tr></thead>
                      <tbody>
                        {doc.lines.map((l) => (
                          <tr key={l.lineNo} data-testid={`post-line-${l.lineNo}`}>
                            <td>{l.lineNo}</td><td><bdi dir="ltr">{l.itemCode}</bdi> — <bdi>{invName({ code: l.itemCode, nameAr: l.itemNameAr, nameEn: l.itemNameEn }, locale)}</bdi></td><td><bdi dir="ltr">{l.baseQuantity} {l.baseUnitCode}</bdi></td>
                            <td>
                              <Field id={`sd-amount-${l.lineNo}`} label={t("stock.posting.amount")}>
                                {(aria) => <input {...aria} value={amounts[l.lineNo] ?? ""} inputMode="decimal" dir="ltr" readOnly={!canPost} onChange={(e) => setAmounts((a) => ({ ...a, [l.lineNo]: e.target.value }))} />}
                              </Field>
                            </td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                  <p data-testid="amount-total">{t("stock.posting.total_preview")}: <bdi dir="ltr">{money(Number(previewTotal.toFixed(6)))}</bdi></p>
                </div>
              ) : <p className="hint">{t("stock.posting.cost_computed")}</p>}
            </div>
          ) : (
            <div data-testid="posting-summary">
              <p><span className={`chip ${posting.accountingState === 3 || posting.accountingState === 2 ? "chip-ok" : posting.accountingState === 4 ? "chip-off" : ""}`} data-testid="accounting-state">{t(`stock.posting.state.${posting.accountingState}`)}</span></p>
              <dl className="details">
                <dt>{t("stock.posting.total")}</dt><dd><bdi dir="ltr" data-testid="posting-total">{money(posting.totalValue)}</bdi></dd>
                <dt>{t("stock.posting.effective")}</dt><dd><bdi dir="ltr">{posting.effectiveDate}</bdi></dd>
                <dt>{t("stock.posting.at")}</dt><dd><bdi dir="ltr">{posting.postedAtUtc.slice(0, 19).replace("T", " ")}</bdi></dd>
                <dt>{t("stock.posting.journal")}</dt>
                <dd data-testid="posting-journal">{posting.journalEntryId ? <><a href="#/accounting/journal"><bdi dir="ltr">{posting.journalEntryId}</bdi></a> — {posting.journalStatus ? t(`stock.posting.journal_status.${posting.journalStatus}`) : ""}</> : t("stock.posting.no_journal")}</dd>
              </dl>
              <div className="table-wrap">
                <table className="data-table">
                  <caption className="visually-hidden">{t("stock.posting.lines")}</caption>
                  <thead><tr><th scope="col">#</th><th scope="col">{t("stock.item")}</th><th scope="col">{t("stock.base_quantity")}</th><th scope="col">{t("stock.posting.amount")}</th></tr></thead>
                  <tbody>
                    {posting.lines.map((l) => {
                      const line = doc.lines.find((x) => x.lineNo === l.lineNo);
                      return <tr key={l.lineNo}><td>{l.lineNo}</td><td><bdi dir="ltr">{line?.itemCode ?? l.itemId}</bdi></td><td><bdi dir="ltr">{l.baseQuantity}</bdi></td><td><bdi dir="ltr">{money(l.amount)}</bdi></td></tr>;
                    })}
                  </tbody>
                </table>
              </div>
              {posting.reversal && (
                <Banner kind="info" testId="reversal-info">
                  {t("stock.reversal")}: <bdi dir="ltr">{posting.reversal.effectiveDate}</bdi> — {t(`stock.reversal.action.${posting.reversal.journalAction}`)} — <bdi>{posting.reversal.reason}</bdi>
                </Banner>
              )}
            </div>
          )}
        </div>
      ),
    } as TabItem] : []),
    ...(doc ? [{
      id: "history", label: t("stock.tab.history"),
      content: (
        <div className="table-wrap">
          <table className="data-table" data-testid="history">
            <caption className="visually-hidden">{t("stock.tab.history")}</caption>
            <thead><tr><th scope="col">{t("stock.event")}</th><th scope="col">{t("stock.status")}</th><th scope="col">{t("stock.event_at")}</th><th scope="col">{t("stock.event_note")}</th></tr></thead>
            <tbody>{doc.events.map((e) => <tr key={e.id}><td>{t(`stock.event.${e.kind}`)}</td><td>{t(`stock.status.${e.toStatus}`)}</td><td><bdi dir="ltr">{e.atUtc.slice(0, 19).replace("T", " ")}</bdi></td><td><bdi>{e.note ?? ""}</bdi></td></tr>)}</tbody>
          </table>
        </div>
      ),
    } as TabItem] : []),
  ];
  const activeTab = tabs.some((x) => x.id === tab) ? tab : "general";
  const rev = doc?.revision ?? 0;

  return (
    <section aria-labelledby="page-title" data-testid="stock-editor">
      <PageHeader title={doc ? (doc.number ?? t("stock.unnumbered")) : t(`stock.new.${form.docType}`)} description={doc ? `${t(`stock.type.${doc.docType}`)} — ${doc.documentDate}` : t("stock.new_subtitle")}
        actions={doc && <span className={`chip ${status === 3 ? "chip-ok" : status >= 4 ? "chip-off" : ""}`} data-testid="doc-status">{t(`stock.status.${status}`)}</span>} />
      <Banner kind="info" testId="no-posting-note">{t("stock.no_posting_note")}</Banner>
      {!canEdit && status === 1 && !isNew && <Banner kind="info" testId="form-read-only">{t("form.read_only")}</Banner>}
      {conflict && <Banner kind="error" testId="form-conflict">{t("stock.error.revision_conflict")} <button type="button" onClick={() => void load()}>{t("form.reload_latest")}</button></Banner>}
      {error && <Banner kind="error" testId="form-error">{error}</Banner>}
      <form onSubmit={save} noValidate>
        <Tabs label={t("stock.tabs_label")} tabs={tabs} value={activeTab} onChange={setTab} testId="stock-tabs" />
        <div className="actions" data-testid="stock-actions">
          <button type="button" onClick={onClose}>{t("inv.cancel")}</button>
          {canEdit && <button type="submit" className="primary" disabled={busy}>{busy ? t("loading") : t("inv.save")}</button>}
          {doc && status === 1 && allDoc(caps.submit) && <button type="button" disabled={busy || dirty} title={dirty ? t("stock.save_first") : undefined} onClick={() => setConfirmSubmit(true)}>{t("stock.submit")}</button>}
          {doc && status === 2 && allDoc(caps.update) && <button type="button" disabled={busy} onClick={() => void act(() => auth.api.withdrawStockDocument(doc.id, rev), t("stock.withdrawn"))}>{t("stock.withdraw")}</button>}
          {doc && status === 2 && allDoc(caps.approve) && <button type="button" className="primary" data-testid="approve" disabled={busy || mine} title={mine ? t("stock.self_approval") : undefined} onClick={() => void act(() => auth.api.approveStockDocument(doc.id, rev), t("stock.approved"))}>{t("stock.approve")}</button>}
          {doc && status === 2 && allDoc(caps.approve) && <button type="button" disabled={busy || mine} onClick={() => { setReason(""); setDialog("reject"); }}>{t("stock.reject")}</button>}
          {canPost && <button type="button" className="primary" data-testid="post" disabled={busy || dirty} onClick={openPost}>{t("stock.post")}</button>}
          {canReverse && <button type="button" className="danger" data-testid="reverse" disabled={busy} onClick={() => { setReason(""); setReverseDate(todayIso()); setDialog("reverse"); }}>{t("stock.reverse")}</button>}
          {doc && (status === 1 || status === 2 || status === 3) && !posting && allDoc(caps.cancel) && <button type="button" className="danger" disabled={busy} onClick={() => { setReason(""); setDialog("cancel"); }}>{t("stock.cancel_document")}</button>}
        </div>
        {doc && status === 2 && mine && allDoc(caps.approve) && <p className="hint" data-testid="self-approval-note">{t("stock.self_approval")}</p>}
      </form>
      {confirmSubmit && doc && <ConfirmDialog title={t("stock.submit")} message={t("stock.submit_message")} confirmLabel={t("form.confirm")} onCancel={() => setConfirmSubmit(false)} onConfirm={() => void act(() => auth.api.submitStockDocument(doc.id, rev), t("stock.submitted"))} />}
      {dialog === "post" && doc && <ConfirmDialog title={t("stock.post")} message={t("stock.post_message")} confirmLabel={t("form.confirm")} onCancel={() => setDialog(null)} onConfirm={() => void act(() => auth.api.postStockDocument(doc.id, rev, postLines()), t("stock.posted"), true)} />}
      {dialog === "reverse" && doc && (
        <ConfirmDialog title={t("stock.reverse")} message={t("stock.reverse_message")} confirmLabel={t("form.confirm")} onCancel={() => setDialog(null)}
          onConfirm={() => { if (!reason.trim() || !reverseDate) { setError(t("stock.reverse_required")); setDialog(null); return; } void act(() => auth.api.reverseStockDocument(doc.id, reverseDate, reason.trim()), t("stock.reversed"), true); }}>
          <Field id="sd-reverse-date" label={t("stock.reverse_date")} required>{(aria) => <input {...aria} type="date" value={reverseDate} onChange={(e) => setReverseDate(e.target.value)} dir="ltr" />}</Field>
          <Field id="sd-reverse-reason" label={t("stock.reverse_reason")} required>{(aria) => <textarea {...aria} value={reason} rows={3} maxLength={500} onChange={(e) => setReason(e.target.value)} />}</Field>
        </ConfirmDialog>
      )}
      {(dialog === "reject" || dialog === "cancel") && doc && (
        <ConfirmDialog title={dialog === "reject" ? t("stock.reject") : t("stock.cancel_document")} message={dialog === "reject" ? t("stock.reject_message") : t("stock.cancel_message")} confirmLabel={t("form.confirm")}
          onCancel={() => setDialog(null)} onConfirm={() => { if (!reason.trim()) { setError(t(dialog === "reject" ? "stock.note_required" : "stock.reason_required")); setDialog(null); return; } void act(() => (dialog === "reject" ? auth.api.rejectStockDocument(doc.id, reason.trim(), rev) : auth.api.cancelStockDocument(doc.id, reason.trim(), rev)), dialog === "reject" ? t("stock.rejected") : t("stock.cancelled")); }}>
          <Field id="sd-reason" label={dialog === "reject" ? t("stock.decision_note") : t("stock.cancel_reason")} required>{(aria) => <textarea {...aria} value={reason} rows={3} maxLength={500} onChange={(e) => setReason(e.target.value)} />}</Field>
        </ConfirmDialog>
      )}
    </section>
  );
}

/** Searches the stockable items on the server (code, name or exact barcode) and lets the user pick one; the picked item carries its exact unit factors. */
function ItemPicker({ id, item, disabled, error, onPick }: { id: string; item: StockItemRef | null; disabled: boolean; error?: string; onPick: (i: StockItemRef) => void }) {
  const { t, locale } = useI18n();
  const auth = useAuth();
  const [term, setTerm] = useState("");
  const [results, setResults] = useState<StockItemRef[]>([]);
  useEffect(() => {
    if (!term.trim()) { setResults([]); return; }
    const h = setTimeout(() => { void auth.api.searchStockItems(term.trim()).then((r) => setResults(r.items)).catch(() => setResults([])); }, 250);
    return () => clearTimeout(h);
  }, [term, auth.api]);
  return (
    <div>
      {item && <div><bdi dir="ltr">{item.code}</bdi> — <bdi>{invName(item, locale)}</bdi></div>}
      {!disabled && (
        <Field id={id} label={t("stock.find_item")} error={error}>
          {(aria) => <input {...aria} type="search" value={term} dir="auto" autoComplete="off" onChange={(e) => setTerm(e.target.value)} />}
        </Field>
      )}
      {!disabled && results.length > 0 && (
        <select aria-label={t("stock.pick_item")} value="" onChange={(e) => { const it = results.find((r) => r.id === e.target.value); if (it) { onPick(it); setTerm(""); setResults([]); } }}>
          <option value="">{t("form.select")}</option>
          {results.map((r) => <option key={r.id} value={r.id}>{r.code} — {invName(r, locale)}</option>)}
        </select>
      )}
      {disabled && error && <p className="field-error">{error}</p>}
    </div>
  );
}
