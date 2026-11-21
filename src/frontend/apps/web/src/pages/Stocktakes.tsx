import { ApiError, type StockItemRef, type StockWarehouseRef, type Stocktake, type StocktakeCapabilities, type StocktakeInput, type StocktakeStatus, type StocktakeSummary } from "@hasm/api-client";
import { toLatinDigits } from "../ledgerCommon";
import { Tabs, useI18n, type TabItem } from "@hasm/ui";
import { useCallback, useEffect, useMemo, useRef, useState, type FormEvent } from "react";
import { useAuth } from "../auth";
import { Banner, Card, ConfirmDialog, Field, Loading, PageHeader } from "../components";
import { useReportUnsaved, useUnsaved } from "../unsaved";
import { blankToNull, invName } from "./inventoryCommon";
import { whName } from "./StockDocuments";
import { StockAdjustmentPanel } from "./StockAdjustmentPanel";

/**
 * Stocktake (Phase 3.8B-1): the count sheet of one warehouse with the book quantity of the costing ledger as of the count date and the exact variance per item, from draft to approval. ONE implementation for
 * Web and Windows Desktop; the screen only drives the server, which enforces every rule (warehouse-scoped permissions, separation of duties, exact conversion, numbering, fiscal period, life cycle, audit).
 * A stocktake changes NO stock and creates NO journal entry. The editor uses the shared Tabs standard (Details, Lines, History); the identity and the workflow actions stay outside the tabs, and switching tabs
 * never saves or resets input.
 */
const PAGE = 25;
const STATUSES: StocktakeStatus[] = [1, 2, 3, 4, 5];
const todayIso = () => { const d = new Date(); const p = (n: number) => String(n).padStart(2, "0"); return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}`; };
/** Display of an exact decimal coming from the server or computed for a preview (trailing zeros and floating-point noise removed). */
const qty = (n: number | null | undefined) => (n === null || n === undefined ? "—" : String(Number(n.toFixed(10))));
const COUNT = /^\d+(\.\d{1,10})?$/;

interface LineRow { key: number; item: StockItemRef; unitId: string; counted: string; reason: string; book: number | null }
interface Form { countDate: string; reference: string; notes: string; lines: LineRow[] }
let rowKey = 0;
const snap = (f: Form) => JSON.stringify({ ...f, lines: f.lines.map((l) => [l.item.id, l.unitId, l.counted, l.reason]) });

type Editor = { mode: "new" } | { mode: "edit"; id: string };

/** Server error texts: the stocktake's own, then the shared stock document texts (period, warehouse, item and unit codes). */
function useErrorText() {
  const { t } = useI18n();
  return useCallback((e: unknown) => {
    if (e instanceof ApiError) {
      for (const k of [`stk.error.${e.code}`, `stock.error.${e.code}`]) if (t(k) !== k) return t(k);
      if (e.status === 403) return t("error.forbidden");
      return t("error.generic");
    }
    return t("error.network");
  }, [t]);
}
function fieldText(t: (k: string) => string, code: string) {
  for (const k of [`stk.field.${code}`, `stock.field.${code}`, `field.${code}`]) if (t(k) !== k) return t(k);
  return t("field.invalid");
}

export function StocktakesPage() {
  const { t, locale } = useI18n();
  const auth = useAuth();
  const errText = useErrorText();
  const errRef = useRef(errText);
  errRef.current = errText;
  const { guard } = useUnsaved();
  const [caps, setCaps] = useState<StocktakeCapabilities | null>(null);
  const [warehouses, setWarehouses] = useState<StockWarehouseRef[] | null>(null);
  const [rows, setRows] = useState<StocktakeSummary[] | null>(null);
  const [total, setTotal] = useState(0);
  const [loadError, setLoadError] = useState<string | null>(null);
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
      const [c, w] = await Promise.all([auth.api.getStocktakeCapabilities(), auth.api.listStocktakeWarehouses()]);
      setCaps(c); setWarehouses(w.warehouses);
    } catch (e) { if (!auth.handleError(e)) setLoadError(errRef.current(e)); }
  }, [auth.api, auth.handleError]);
  useEffect(() => { void loadRefs(); }, [loadRefs]);

  const loadList = useCallback(async () => {
    try {
      const page = await auth.api.listStocktakes({ status: status ? (Number(status) as StocktakeStatus) : undefined, warehouseId: warehouse || undefined, from: from || undefined, to: to || undefined, search: search.trim() || undefined, offset, limit: PAGE });
      setRows(page.stocktakes); setTotal(page.total); setLoadError(null);
    } catch (e) { if (!auth.handleError(e)) setLoadError(errRef.current(e)); }
  }, [auth.api, auth.handleError, status, warehouse, from, to, search, offset]);
  useEffect(() => { const h = setTimeout(() => void loadList(), 250); return () => clearTimeout(h); }, [loadList]);

  const whById = useMemo(() => new Map((warehouses ?? []).map((w) => [w.id, w])), [warehouses]);
  if (loadError) return <Banner kind="error">{loadError}</Banner>;
  if (!caps || !warehouses || !rows) return <Loading />;

  if (editor) {
    return (
      <StocktakeEditor key={editor.mode === "edit" ? editor.id : "new"} editor={editor} caps={caps} warehouses={warehouses}
        onClose={() => guard(() => { setEditor(null); void loadList(); })} onChanged={(m) => { setNotice(m); void loadList(); }} onSwitch={(id) => setEditor({ mode: "edit", id })} />
    );
  }
  const canCreate = caps.create.length > 0;
  return (
    <section aria-labelledby="page-title">
      <PageHeader title={t("stk.title")} description={t("stk.subtitle")}
        actions={canCreate && <button type="button" className="primary" onClick={() => guard(() => { setNotice(null); setEditor({ mode: "new" }); })}>{t("stk.new")}</button>} />
      <Banner kind="info" testId="stk-no-posting">{t("stk.no_posting_note")}</Banner>
      {!canCreate && <Banner kind="info" testId="read-only">{t("form.read_only")}</Banner>}
      {notice && <Banner kind="success" testId="saved">{notice}</Banner>}
      <Card title={t("stk.list")}>
        <div className="toolbar">
          <Field id="stk-search" label={t("stk.search")} hint={t("stk.search_hint")}>{(aria) => <input {...aria} type="search" value={search} onChange={(e) => { setOffset(0); setSearch(e.target.value); }} autoComplete="off" />}</Field>
          <Field id="stk-status" label={t("stk.status")}>
            {(aria) => <select {...aria} value={status} onChange={(e) => { setOffset(0); setStatus(e.target.value); }}><option value="">{t("inv.filter_all")}</option>{STATUSES.map((x) => <option key={x} value={x}>{t(`stk.status.${x}`)}</option>)}</select>}
          </Field>
          <Field id="stk-warehouse" label={t("stk.warehouse")}>
            {(aria) => <select {...aria} value={warehouse} onChange={(e) => { setOffset(0); setWarehouse(e.target.value); }}><option value="">{t("inv.filter_all")}</option>{warehouses.map((w) => <option key={w.id} value={w.id}>{whName(w, locale)}</option>)}</select>}
          </Field>
          <Field id="stk-from" label={t("stk.from")}>{(aria) => <input {...aria} type="date" value={from} onChange={(e) => { setOffset(0); setFrom(e.target.value); }} dir="ltr" />}</Field>
          <Field id="stk-to" label={t("stk.to")}>{(aria) => <input {...aria} type="date" value={to} onChange={(e) => { setOffset(0); setTo(e.target.value); }} dir="ltr" />}</Field>
        </div>
        <div className="table-wrap">
          <table className="data-table">
            <caption className="visually-hidden">{t("stk.list")}</caption>
            <thead><tr><th scope="col">{t("stk.number")}</th><th scope="col">{t("stk.count_date")}</th><th scope="col">{t("stk.warehouse")}</th><th scope="col">{t("stk.reference")}</th><th scope="col">{t("stk.lines_counted")}</th><th scope="col">{t("stk.variance_lines")}</th><th scope="col">{t("stk.status")}</th><th scope="col">{t("inv.actions")}</th></tr></thead>
            <tbody>
              {rows.length === 0 && <tr><td colSpan={8}>{total === 0 && !search && !status && !warehouse && !from && !to ? t("stk.none") : t("stk.none_filtered")}</td></tr>}
              {rows.map((d) => (
                <tr key={d.id} data-testid={`stk-${d.id}`}>
                  <td><bdi dir="ltr">{d.number ?? t("stk.unnumbered")}</bdi></td><td><bdi dir="ltr">{d.countDate}</bdi></td><td><bdi>{whName(whById.get(d.warehouseId), locale)}</bdi></td><td><bdi>{d.reference ?? ""}</bdi></td>
                  <td className="num">{t("stk.counted_of", { counted: String(d.countedLines), total: String(d.lineCount) })}</td><td className="num">{d.varianceLines}</td>
                  <td><span className={`chip ${d.status === 3 ? "chip-ok" : d.status >= 4 ? "chip-off" : ""}`}>{t(`stk.status.${d.status}`)}</span></td>
                  <td><button type="button" onClick={() => guard(() => { setNotice(null); setEditor({ mode: "edit", id: d.id }); })}>{t("inv.view")}</button></td>
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

const lineOf = (l: Stocktake["lines"][number], known: Map<string, StockItemRef>): LineRow => ({
  key: ++rowKey, unitId: l.unitId, counted: l.countedQuantity === null ? "" : qty(l.countedQuantity), reason: l.reason ?? "", book: l.bookQuantity,
  // the server returns the units of every line (follow-up M2 of the 3.8B-1 review), so a reloaded line can be counted in any unit of its item, not only the saved one
  item: { id: l.itemId, code: l.itemCode, nameAr: l.itemNameAr, nameEn: l.itemNameEn, units: l.units?.length ? l.units : known.get(l.itemId)?.units ?? [{ id: l.unitId, code: l.unitCode, nameAr: null, nameEn: null, decimals: 10, factor: l.factor, isBase: l.factor === 1 }] },
});
const formOf = (d: Stocktake, known: Map<string, StockItemRef>): Form => ({ countDate: d.countDate, reference: d.reference ?? "", notes: d.notes ?? "", lines: d.lines.map((l) => lineOf(l, known)) });
const TAB_OF = (field: string): "details" | "lines" => (field.startsWith("lines") || field === "itemIds" ? "lines" : "details");

function StocktakeEditor({ editor, caps, warehouses, onClose, onChanged, onSwitch }: {
  editor: Editor; caps: StocktakeCapabilities; warehouses: StockWarehouseRef[]; onClose: () => void; onChanged: (m: string) => void; onSwitch: (id: string) => void;
}) {
  const { t, locale } = useI18n();
  const auth = useAuth();
  const errText = useErrorText();
  const isNew = editor.mode === "new";
  const [doc, setDoc] = useState<Stocktake | null>(null);
  const [loading, setLoading] = useState(!isNew);
  const [warehouseId, setWarehouseId] = useState("");
  const [form, setForm] = useState<Form>({ countDate: todayIso(), reference: "", notes: "", lines: [] });
  const [base, setBase] = useState(() => snap(form));
  const [known] = useState(() => new Map<string, StockItemRef>());
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [error, setError] = useState<string | null>(null);
  const [conflict, setConflict] = useState(false);
  const [busy, setBusy] = useState(false);
  const [tab, setTab] = useState("details");
  const [dialog, setDialog] = useState<null | "submit" | "reject" | "cancel" | "adjust">(null);
  const [reason, setReason] = useState("");

  const load = useCallback(async () => {
    if (editor.mode !== "edit") return;
    try {
      const d = await auth.api.getStocktake(editor.id);
      const f = formOf(d, known);
      setDoc(d); setWarehouseId(d.warehouseId); setForm(f); setBase(snap(f)); setErrors({}); setConflict(false); setLoading(false);
    } catch (e) { if (!auth.handleError(e)) { setError(errText(e)); setLoading(false); } }
  }, [auth.api, auth.handleError, editor, errText, known]);
  useEffect(() => { void load(); }, [load]);

  const dirty = snap(form) !== base;
  useReportUnsaved("stocktake-form", dirty, t("stk.form_section"));
  const status = doc?.status ?? 1;
  const on = (ids: string[]) => !!doc && ids.includes(doc.warehouseId);
  const canEdit = isNew ? caps.create.length > 0 : status === 1 && on(caps.update);
  const mine = doc?.submittedBy === auth.user?.id;
  const setLine = (key: number, patch: Partial<LineRow>) => setForm((f) => ({ ...f, lines: f.lines.map((r) => (r.key === key ? { ...r, ...patch } : r)) }));

  function validate(): Record<string, string> {
    const v: Record<string, string> = {};
    if (isNew && !warehouseId) v.warehouseId = t("field.required");
    if (!form.countDate) v.countDate = t("field.required");
    form.lines.forEach((l, i) => { if (l.counted.trim() && !COUNT.test(l.counted.trim())) v[`lines[${i}].countedQuantity`] = fieldText(t, "invalid_quantity"); });
    return v;
  }
  const invalidTabs = (e: Record<string, string>) => new Set(Object.keys(e).map(TAB_OF));
  const toInput = (): StocktakeInput => ({
    warehouseId, countDate: form.countDate, reference: blankToNull(form.reference), notes: blankToNull(form.notes),
    lines: form.lines.map((l) => ({ itemId: l.item.id, unitId: l.unitId, countedQuantity: l.counted.trim() ? Number(l.counted.trim()) : null, reason: blankToNull(l.reason) })),
  });

  function showServerError(err: unknown) {
    if (auth.handleError(err)) return;
    if (err instanceof ApiError && err.status === 412) { setConflict(true); return; }
    if (err instanceof ApiError && err.status === 400) {
      const out: Record<string, string> = {};
      for (const f of err.details.fields ?? []) out[f.field] = fieldText(t, f.code);
      setErrors(out); setError(t("form.fix_errors"));
      const first = (["details", "lines"] as const).find((id) => invalidTabs(out).has(id));
      if (first) setTab(first);
      return;
    }
    setError(errText(err));
  }

  async function save(e: FormEvent) {
    e.preventDefault();
    if (!canEdit) return;
    const v = validate();
    setErrors(v);
    if (Object.keys(v).length > 0) { setError(t("form.fix_errors")); const first = (["details", "lines"] as const).find((id) => invalidTabs(v).has(id)); if (first) setTab(first); return; }
    setBusy(true); setError(null); setConflict(false);
    try {
      if (editor.mode === "new") { const d = await auth.api.createStocktake(toInput()); onChanged(t("stk.created")); setBase(snap(form)); onSwitch(d.id); }
      else { await auth.api.updateStocktake(editor.id, toInput(), doc!.revision); onChanged(t("stk.saved")); await load(); }
    } catch (err) { showServerError(err); } finally { setBusy(false); }
  }

  async function act(run: () => Promise<unknown>, message: string) {
    setBusy(true); setError(null); setConflict(false); setDialog(null);
    try { await run(); onChanged(message); await load(); setReason(""); }
    catch (err) { showServerError(err); } finally { setBusy(false); }
  }

  if (loading) return <Loading />;
  const invalid = invalidTabs(errors);
  const rev = doc?.revision ?? 0;
  const tabs: TabItem[] = [
    {
      id: "details", label: t("stk.tab.details"), invalid: invalid.has("details"),
      content: (
        <div className="fields">
          <Field id="stk-wh" label={t("stk.warehouse")} error={errors.warehouseId} required={isNew} hint={t("stk.warehouse_hint")}>
            {(aria) => isNew
              ? <select {...aria} value={warehouseId} onChange={(e) => setWarehouseId(e.target.value)}><option value="">{t("form.select")}</option>{warehouses.filter((w) => w.isActive && caps.create.includes(w.id)).map((w) => <option key={w.id} value={w.id}>{whName(w, locale)}</option>)}</select>
              : <input {...aria} readOnly value={whName(warehouses.find((w) => w.id === warehouseId), locale)} />}
          </Field>
          <Field id="stk-date" label={t("stk.count_date")} error={errors.countDate} required hint={t("stk.count_date_hint")}>{(aria) => <input {...aria} type="date" value={form.countDate} readOnly={!canEdit} onChange={(e) => setForm((f) => ({ ...f, countDate: e.target.value }))} dir="ltr" />}</Field>
          <Field id="stk-ref" label={t("stk.reference")} error={errors.reference}>{(aria) => <input {...aria} value={form.reference} readOnly={!canEdit} maxLength={100} onChange={(e) => setForm((f) => ({ ...f, reference: e.target.value }))} autoComplete="off" />}</Field>
          <Field id="stk-notes" label={t("stk.notes")} error={errors.notes}>{(aria) => <textarea {...aria} value={form.notes} readOnly={!canEdit} rows={3} maxLength={1000} onChange={(e) => setForm((f) => ({ ...f, notes: e.target.value }))} />}</Field>
          {doc && <Field id="stk-snap" label={t("stk.snapshot_at")}>{(aria) => <input {...aria} readOnly dir="ltr" value={doc.snapshotAtUtc.slice(0, 19).replace("T", " ")} />}</Field>}
          {doc && (doc.decisionNote || doc.cancelReason) && <Field id="stk-decision" label={doc.cancelReason ? t("stk.cancel_reason") : t("stk.decision_note")}>{(aria) => <input {...aria} readOnly value={doc.cancelReason ?? doc.decisionNote ?? ""} />}</Field>}
        </div>
      ),
    },
    ...(doc ? [{
      id: "lines", label: t("stk.tab.lines"), invalid: invalid.has("lines"),
      content: (
        <div>
          {errors.lines && <Banner kind="error">{errors.lines}</Banner>}
          {errors.itemIds && <Banner kind="error">{errors.itemIds}</Banner>}
          <p className="hint">{t("stk.lines_hint")}</p>
          {canEdit && (
            <div className="toolbar">
              <button type="button" data-testid="generate" disabled={busy || dirty} title={dirty ? t("stk.save_first") : t("stk.generate_hint")} onClick={() => void act(() => auth.api.generateStocktakeSheet(doc.id, rev), t("stk.generated"))}>{t("stk.generate")}</button>
              <ItemPicker onPick={(item) => { known.set(item.id, item); if (!form.lines.some((l) => l.item.id === item.id)) setForm((f) => ({ ...f, lines: [...f.lines, { key: ++rowKey, item, unitId: item.units.find((u) => u.isBase)?.id ?? item.units[0]?.id ?? "", counted: "", reason: "", book: null }] })); }} />
            </div>
          )}
          <div className="table-wrap">
            <table className="data-table">
              <caption className="visually-hidden">{t("stk.tab.lines")}</caption>
              <thead><tr><th scope="col">#</th><th scope="col">{t("stk.item")}</th><th scope="col">{t("stk.unit")}</th><th scope="col">{t("stk.counted")}</th><th scope="col">{t("stk.counted_base")}</th><th scope="col">{t("stk.book")}</th><th scope="col">{t("stk.variance")}</th><th scope="col">{t("stk.reason")}</th><th scope="col">{t("inv.actions")}</th></tr></thead>
              <tbody>
                {form.lines.map((l, i) => {
                  const saved = !dirty ? doc.lines[i] : undefined;
                  const unit = l.item.units.find((u) => u.id === l.unitId);
                  const c = l.counted.trim();
                  const previewBase = unit && COUNT.test(c) ? Number(c) * unit.factor : null;
                  const countedBase = saved ? saved.countedBaseQuantity : previewBase;
                  const book = saved ? saved.bookQuantity : l.book;
                  const variance = saved ? saved.variance : countedBase !== null && book !== null ? countedBase - book : null;
                  const baseCode = saved?.baseUnitCode ?? l.item.units.find((u) => u.isBase)?.code ?? "";
                  return (
                    <tr key={l.key} data-testid={`stk-line-${i}`}>
                      <td>{i + 1}</td>
                      <td><bdi dir="ltr">{l.item.code}</bdi> — <bdi>{invName(l.item, locale)}</bdi></td>
                      <td>
                        <Field id={`stk-unit-${i}`} label={t("stk.unit")} error={errors[`lines[${i}].unitId`]}>
                          {(aria) => <select {...aria} value={l.unitId} disabled={!canEdit} onChange={(e) => setLine(l.key, { unitId: e.target.value })}>{l.item.units.map((u) => <option key={u.id} value={u.id}>{u.code}{u.isBase ? "" : ` (×${u.factor})`}</option>)}</select>}
                        </Field>
                      </td>
                      <td>
                        <Field id={`stk-qty-${i}`} label={t("stk.counted")} error={errors[`lines[${i}].countedQuantity`] ?? errors[`lines[${i}].itemId`]}>
                          {(aria) => <input {...aria} value={l.counted} inputMode="decimal" dir="ltr" readOnly={!canEdit} onChange={(e) => setLine(l.key, { counted: toLatinDigits(e.target.value) })} />}
                        </Field>
                      </td>
                      <td><bdi dir="ltr" data-testid={`stk-base-${i}`}>{countedBase === null || countedBase === undefined ? "—" : `${qty(countedBase)} ${baseCode}`}</bdi></td>
                      <td>
                        <bdi dir="ltr" data-testid={`stk-book-${i}`}>{book === null ? "—" : `${qty(book)} ${baseCode}`}</bdi>
                        {saved?.stale && status <= 3 && <> <span className="chip chip-off" title={`${t("stk.current_book")}: ${qty(saved.currentBookQuantity)}`} data-testid={`stk-stale-${i}`}>{t("stk.stale_chip")}</span></>}
                      </td>
                      <td><bdi dir="ltr" data-testid={`stk-variance-${i}`} className={variance ? "field-error" : undefined}>{variance === null || variance === undefined ? "—" : qty(variance)}</bdi></td>
                      <td>
                        <Field id={`stk-reason-${i}`} label={t("stk.reason")} error={errors[`lines[${i}].reason`]}>
                          {(aria) => <input {...aria} value={l.reason} readOnly={!canEdit} maxLength={200} onChange={(e) => setLine(l.key, { reason: e.target.value })} />}
                        </Field>
                      </td>
                      <td>{canEdit && <button type="button" onClick={() => setForm((f) => ({ ...f, lines: f.lines.filter((r) => r.key !== l.key) }))}>{t("stk.remove")}</button>}</td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        </div>
      ),
    } as TabItem, {
      id: "history", label: t("stk.tab.history"),
      content: (
        <div className="table-wrap">
          <table className="data-table" data-testid="stk-history">
            <caption className="visually-hidden">{t("stk.tab.history")}</caption>
            <thead><tr><th scope="col">{t("stk.event")}</th><th scope="col">{t("stk.status")}</th><th scope="col">{t("stk.event_at")}</th><th scope="col">{t("stk.event_note")}</th></tr></thead>
            <tbody>{doc.events.map((e) => <tr key={e.id}><td>{t(`stk.event.${e.kind}`)}</td><td>{t(`stk.status.${e.toStatus}`)}</td><td><bdi dir="ltr">{e.atUtc.slice(0, 19).replace("T", " ")}</bdi></td><td><bdi>{e.note ?? ""}</bdi></td></tr>)}</tbody>
          </table>
        </div>
      ),
    } as TabItem] : []),
    // Phase 3.8B-2: the one adjustment of the approved stocktake (generated with the action outside the tabs; posted and reversed here)
    ...(doc?.adjustmentId ? [{
      id: "adjustment", label: t("stk.tab.adjustment"),
      content: <StockAdjustmentPanel id={doc.adjustmentId} canPost={on(caps.post ?? [])} canReverse={on(caps.reverse ?? [])} onChanged={onChanged} />,
    } as TabItem] : []),
  ];
  const activeTab = tabs.some((x) => x.id === tab) ? tab : "details";

  return (
    <section aria-labelledby="page-title" data-testid="stk-editor">
      <PageHeader title={doc ? (doc.number ?? t("stk.unnumbered")) : t("stk.new")} description={doc ? `${whName(warehouses.find((w) => w.id === doc.warehouseId), locale)} — ${doc.countDate}` : t("stk.new_subtitle")}
        actions={doc && <span className={`chip ${status === 3 ? "chip-ok" : status >= 4 ? "chip-off" : ""}`} data-testid="stk-status">{t(`stk.status.${status}`)}</span>} />
      <Banner kind="info" testId="stk-no-posting">{t("stk.no_posting_note")}</Banner>
      {doc?.stale && status <= 3 && <Banner kind="warning" testId="stk-stale">{t("stk.stale")}</Banner>}
      {conflict && <Banner kind="error" testId="form-conflict">{t("stk.error.revision_conflict")} <button type="button" onClick={() => void load()}>{t("form.reload_latest")}</button></Banner>}
      {error && <Banner kind="error" testId="form-error">{error}</Banner>}
      <form onSubmit={save} noValidate>
        <Tabs label={t("stk.tabs_label")} tabs={tabs} value={activeTab} onChange={setTab} testId="stk-tabs" />
        <div className="actions" data-testid="stk-actions">
          <button type="button" onClick={onClose}>{t("inv.cancel")}</button>
          {canEdit && <button type="submit" className="primary" disabled={busy}>{busy ? t("loading") : isNew ? t("stk.create") : t("inv.save")}</button>}
          {doc && status === 1 && on(caps.submit) && <button type="button" disabled={busy || dirty} title={dirty ? t("stk.save_first") : undefined} onClick={() => setDialog("submit")}>{t("stk.submit")}</button>}
          {doc && status === 2 && on(caps.update) && <button type="button" disabled={busy} onClick={() => void act(() => auth.api.withdrawStocktake(doc.id, rev), t("stk.withdrawn"))}>{t("stk.withdraw")}</button>}
          {doc && status === 2 && on(caps.approve) && <button type="button" className="primary" data-testid="stk-approve" disabled={busy || mine} title={mine ? t("stk.self_approval") : undefined} onClick={() => void act(() => auth.api.approveStocktake(doc.id, rev), t("stk.approved"))}>{t("stk.approve")}</button>}
          {doc && status === 2 && on(caps.approve) && <button type="button" disabled={busy || mine} onClick={() => { setReason(""); setDialog("reject"); }}>{t("stk.reject")}</button>}
          {doc && status === 3 && !doc.adjustmentId && on(caps.adjust ?? []) && <button type="button" className="primary" data-testid="stk-adjust" disabled={busy} title={t("stk.adjust_message")} onClick={() => setDialog("adjust")}>{t("stk.adjust")}</button>}
          {doc && status <= 3 && !doc.adjustmentId && on(caps.cancel) && <button type="button" className="danger" disabled={busy} onClick={() => { setReason(""); setDialog("cancel"); }}>{t("stk.cancel")}</button>}
        </div>
        {doc && status === 2 && mine && on(caps.approve) && <p className="hint" data-testid="stk-self-approval">{t("stk.self_approval")}</p>}
        {doc?.adjustmentId && on(caps.cancel) && <p className="hint" data-testid="stk-cancel-blocked">{t("stk.adj.cancel_blocked")}</p>}
      </form>
      {dialog === "adjust" && doc && <ConfirmDialog title={t("stk.adjust")} message={t("stk.adjust_message")} confirmLabel={t("form.confirm")} onCancel={() => setDialog(null)} onConfirm={() => void act(() => auth.api.generateStockAdjustment(doc.id, rev), t("stk.adjusted")).then(() => setTab("adjustment"))} />}
      {dialog === "submit" && doc && <ConfirmDialog title={t("stk.submit")} message={t("stk.submit_message")} confirmLabel={t("form.confirm")} onCancel={() => setDialog(null)} onConfirm={() => void act(() => auth.api.submitStocktake(doc.id, rev), t("stk.submitted"))} />}
      {(dialog === "reject" || dialog === "cancel") && doc && (
        <ConfirmDialog title={dialog === "reject" ? t("stk.reject") : t("stk.cancel")} message={dialog === "reject" ? t("stk.reject_message") : t("stk.cancel_message")} confirmLabel={t("form.confirm")}
          onCancel={() => setDialog(null)} onConfirm={() => { if (!reason.trim()) { setError(t(dialog === "reject" ? "stk.note_required" : "stk.reason_required")); setDialog(null); return; } void act(() => (dialog === "reject" ? auth.api.rejectStocktake(doc.id, reason.trim(), rev) : auth.api.cancelStocktake(doc.id, reason.trim(), rev)), dialog === "reject" ? t("stk.rejected") : t("stk.cancelled")); }}>
          <Field id="stk-dialog-reason" label={dialog === "reject" ? t("stk.decision_note") : t("stk.cancel_reason")} required>{(aria) => <textarea {...aria} value={reason} rows={3} maxLength={500} onChange={(e) => setReason(e.target.value)} />}</Field>
        </ConfirmDialog>
      )}
    </section>
  );
}

/** Searches the countable items on the server (code, name or exact barcode); the picked item carries its exact unit factors. */
function ItemPicker({ onPick }: { onPick: (i: StockItemRef) => void }) {
  const { t, locale } = useI18n();
  const auth = useAuth();
  const [term, setTerm] = useState("");
  const [results, setResults] = useState<StockItemRef[]>([]);
  useEffect(() => {
    if (!term.trim()) { setResults([]); return; }
    const h = setTimeout(() => { void auth.api.searchStocktakeItems(term.trim()).then((r) => setResults(r.items)).catch(() => setResults([])); }, 250);
    return () => clearTimeout(h);
  }, [term, auth.api]);
  return (
    <div>
      <Field id="stk-find" label={t("stk.find_item")}>{(aria) => <input {...aria} type="search" value={term} dir="auto" autoComplete="off" onChange={(e) => setTerm(e.target.value)} />}</Field>
      {results.length > 0 && (
        <select aria-label={t("stk.pick_item")} value="" onChange={(e) => { const it = results.find((r) => r.id === e.target.value); if (it) { onPick(it); setTerm(""); setResults([]); } }}>
          <option value="">{t("form.select")}</option>
          {results.map((r) => <option key={r.id} value={r.id}>{r.code} — {invName(r, locale)}</option>)}
        </select>
      )}
    </div>
  );
}
