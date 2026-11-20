import {
  ApiError, type InventoryBarcode, type InventoryCapabilities, type InventoryGroup, type InventoryItem, type InventoryItemInput, type InventoryUnit, type ItemTypeCode, type ValuationMethodCode,
} from "@hasm/api-client";
import { Tabs, useI18n, type TabItem } from "@hasm/ui";
import { useCallback, useEffect, useMemo, useRef, useState, type FormEvent } from "react";
import { useAuth } from "../auth";
import { Banner, Card, ConfirmDialog, errorText, Field, Loading, PageHeader } from "../components";
import { navigate } from "../router";
import { useReportUnsaved, useUnsaved } from "../unsaved";
import { TaxClassPicker } from "./TaxClassPicker";
import { blankToNull, groupTree, invFieldErrors, invName, unitLabel } from "./inventoryCommon";

/**
 * Items (Phase 3.4): the item master. ONE implementation for Web and Windows Desktop. The editor uses the shared Tabs standard (General information, Units and conversions, Tracking settings, Barcodes): the item
 * being edited and Save/Cancel stay visible whichever tab is active, and switching tabs never saves, discards or resets input. The Barcodes tab exists for a saved item only and its changes are applied
 * immediately (they are their own audited operations). All rules (codes, units and conversions, valuation policy, barcodes, concurrency, audit, authorization) are enforced by the server and PostgreSQL; this
 * screen validates for convenience only. There is NO stock quantity or cost here: those belong to the later stock phase.
 */
const CODE_SHAPE = /^[A-Z0-9][A-Z0-9_-]{0,29}$/;
const PAGE = 25;

interface AltRow { key: number; unitId: string; factor: string }
interface Form {
  code: string; nameAr: string; nameEn: string; itemType: ItemTypeCode; groupId: string; notes: string; baseUnitId: string; purchaseUnitId: string; salesUnitId: string;
  isLot: boolean; isSerial: boolean; hasExpiry: boolean; alts: AltRow[];
}
let rowKey = 0;
const toForm = (i: InventoryItem | null): Form => ({
  code: i?.code ?? "", nameAr: i?.nameAr ?? "", nameEn: i?.nameEn ?? "", itemType: i?.itemType ?? 1, groupId: i?.groupId ?? "", notes: i?.notes ?? "", baseUnitId: i?.baseUnitId ?? "",
  purchaseUnitId: i?.purchaseUnitId ?? "", salesUnitId: i?.salesUnitId ?? "", isLot: i?.isLot ?? false, isSerial: i?.isSerial ?? false, hasExpiry: i?.hasExpiry ?? false,
  alts: (i?.alternateUnits ?? []).map((a) => ({ key: ++rowKey, unitId: a.unitId, factor: String(a.factor) })),
});
const snapshot = (f: Form) => JSON.stringify({ ...f, alts: f.alts.map((a) => [a.unitId, a.factor]) });

type Editor = { mode: "new" } | { mode: "edit"; item: InventoryItem };
const TAB_OF_FIELD = (field: string): "general" | "units" | "tracking" =>
  /^(baseUnitId|purchaseUnitId|salesUnitId|alternateUnits)/.test(field) ? "units" : /^(isLot|isSerial|hasExpiry)$/.test(field) ? "tracking" : "general";

export function InventoryItemsPage() {
  const { t, locale } = useI18n();
  const tRef = useRef(t);
  tRef.current = t;
  const auth = useAuth();
  const { guard } = useUnsaved();
  const [caps, setCaps] = useState<InventoryCapabilities | null>(null);
  const [groups, setGroups] = useState<InventoryGroup[] | null>(null);
  const [units, setUnits] = useState<InventoryUnit[] | null>(null);
  const [items, setItems] = useState<InventoryItem[] | null>(null);
  const [total, setTotal] = useState(0);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [search, setSearch] = useState("");
  const [groupId, setGroupId] = useState("");
  const [status, setStatus] = useState<"all" | "active" | "inactive">("all");
  const [type, setType] = useState<"" | "1" | "2">("");
  const [offset, setOffset] = useState(0);
  const [editor, setEditor] = useState<Editor | null>(null);
  const [confirm, setConfirm] = useState<InventoryItem | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [conflict, setConflict] = useState(false);
  const [busy, setBusy] = useState(false);

  const loadRefs = useCallback(async () => {
    try {
      const [c, g, u] = await Promise.all([auth.api.getInventoryCapabilities(), auth.api.listInventoryGroups(), auth.api.listInventoryUnits()]);
      setCaps(c); setGroups(g.groups); setUnits(u.units);
    } catch (e) { if (!auth.handleError(e)) setLoadError(errorText(tRef.current, e, "inv.error.")); }
  }, [auth.api, auth.handleError]);
  useEffect(() => { void loadRefs(); }, [loadRefs]);

  const loadItems = useCallback(async () => {
    try {
      const page = await auth.api.listInventoryItems({
        search: search.trim() || undefined, groupId: groupId || undefined, active: status === "all" ? undefined : status === "active", type: type === "" ? undefined : (Number(type) as ItemTypeCode), offset, limit: PAGE,
      });
      setItems(page.items); setTotal(page.total); setLoadError(null);
    } catch (e) { if (!auth.handleError(e)) setLoadError(errorText(tRef.current, e, "inv.error.")); }
  }, [auth.api, auth.handleError, search, groupId, status, type, offset]);
  useEffect(() => { const h = setTimeout(() => void loadItems(), 250); return () => clearTimeout(h); }, [loadItems]);

  const groupOf = useMemo(() => new Map((groups ?? []).map((g) => [g.id, g])), [groups]);
  if (loadError) return <Banner kind="error">{loadError}</Banner>;
  if (!caps || !groups || !units || !items) return <Loading />;
  const method = (m: ValuationMethodCode | null) => (m === 1 ? t("inv.valuation.fifo") : m === 2 ? t("inv.valuation.average") : "—");

  async function toggle(i: InventoryItem) {
    setConfirm(null);
    setBusy(true); setError(null); setConflict(false); setNotice(null);
    try {
      await (i.isActive ? auth.api.deactivateInventoryItem(i.id, i.revision) : auth.api.activateInventoryItem(i.id, i.revision));
      await loadItems();
      setNotice(t("inv.changed"));
    } catch (err) {
      if (auth.handleError(err)) return;
      if (err instanceof ApiError && err.status === 412) setConflict(true); else setError(errorText(t, err, "inv.error."));
    } finally { setBusy(false); }
  }

  if (editor) {
    return (
      <ItemEditor key={editor.mode === "edit" ? editor.item.id : "new"} editor={editor} groups={groups} units={units} caps={caps}
        onClose={() => guard(() => setEditor(null))} onSaved={(m) => { setEditor(null); setNotice(m); void loadItems(); }} />
    );
  }

  return (
    <section aria-labelledby="page-title">
      <PageHeader title={t("inv.items.title")} description={t("inv.items.subtitle")}
        actions={<>{auth.can("company.imports.upload") && auth.can("company.items.create") && <button type="button" disabled={busy} onClick={() => guard(() => navigate("/company/imports"))}>{t("inv.import")}</button>}{caps.itemsCreate && <button type="button" className="primary" disabled={busy} onClick={() => guard(() => { setNotice(null); setEditor({ mode: "new" }); })}>{t("inv.items.new")}</button>}</>} />
      <Banner kind="info" testId="no-stock-note">{t("inv.items.no_stock_note")}</Banner>
      {!caps.itemsCreate && !caps.itemsUpdate && <Banner kind="info" testId="read-only">{t("form.read_only")}</Banner>}
      {conflict && <Banner kind="error" testId="conflict">{t("inv.error.revision_conflict")} <button type="button" onClick={() => { setConflict(false); void loadItems(); }}>{t("form.reload_latest")}</button></Banner>}
      {error && <Banner kind="error">{error}</Banner>}
      {notice && <Banner kind="success" testId="saved">{notice}</Banner>}
      <Card title={t("inv.items.list")}>
        <div className="toolbar">
          <Field id="i-search" label={t("inv.items.search")} hint={t("inv.items.search_hint")}>
            {(aria) => <input {...aria} type="search" value={search} onChange={(e) => { setOffset(0); setSearch(e.target.value); }} autoComplete="off" />}
          </Field>
          <Field id="i-group" label={t("inv.items.filter_group")}>
            {(aria) => (
              <select {...aria} value={groupId} onChange={(e) => { setOffset(0); setGroupId(e.target.value); }}>
                <option value="">{t("inv.items.all_groups")}</option>
                {groupTree(groups).map(({ group: g, depth }) => <option key={g.id} value={g.id}>{"  ".repeat(depth)}{g.code} — {invName(g, locale)}</option>)}
              </select>
            )}
          </Field>
          <Field id="i-status" label={t("inv.status")}>
            {(aria) => (
              <select {...aria} value={status} onChange={(e) => { setOffset(0); setStatus(e.target.value as typeof status); }}>
                <option value="all">{t("inv.filter_all")}</option><option value="active">{t("inv.active")}</option><option value="inactive">{t("inv.inactive")}</option>
              </select>
            )}
          </Field>
          <Field id="i-type" label={t("inv.items.type")}>
            {(aria) => (
              <select {...aria} value={type} onChange={(e) => { setOffset(0); setType(e.target.value as typeof type); }}>
                <option value="">{t("inv.filter_all")}</option><option value="1">{t("inv.items.goods")}</option><option value="2">{t("inv.items.service")}</option>
              </select>
            )}
          </Field>
        </div>
        <div className="table-wrap">
          <table className="data-table">
            <caption className="visually-hidden">{t("inv.items.list")}</caption>
            <thead><tr><th scope="col">{t("inv.code")}</th><th scope="col">{t("inv.name")}</th><th scope="col">{t("inv.items.group")}</th><th scope="col">{t("inv.items.type")}</th><th scope="col">{t("inv.groups.valuation")}</th><th scope="col">{t("inv.status")}</th><th scope="col">{t("inv.actions")}</th></tr></thead>
            <tbody>
              {items.length === 0 && <tr><td colSpan={7}>{total === 0 && !search && !groupId && status === "all" && !type ? t("inv.items.none") : t("inv.items.none_filtered")}</td></tr>}
              {items.map((i) => {
                const g = groupOf.get(i.groupId);
                return (
                  <tr key={i.id} data-testid={`item-${i.code}`}>
                    <td><bdi dir="ltr">{i.code}</bdi></td><td><bdi>{invName(i, locale)}</bdi></td><td><bdi>{g ? invName(g, locale) : "—"}</bdi></td>
                    <td>{i.itemType === 1 ? t("inv.items.goods") : t("inv.items.service")}</td><td>{method(i.effectiveValuation)}</td>
                    <td><span className={`chip ${i.isActive ? "chip-ok" : "chip-off"}`}>{i.isActive ? t("inv.active") : t("inv.inactive")}</span></td>
                    <td>
                      <button type="button" disabled={busy} onClick={() => guard(() => { setNotice(null); setEditor({ mode: "edit", item: i }); })}>{caps.itemsUpdate ? t("inv.edit") : t("inv.view")}</button>{" "}
                      {caps.itemsActivate && <button type="button" disabled={busy} onClick={() => setConfirm(i)}>{i.isActive ? t("inv.deactivate") : t("inv.activate")}</button>}
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
        <div className="toolbar" data-testid="paging">
          <button type="button" disabled={offset === 0} onClick={() => setOffset(Math.max(0, offset - PAGE))}>{t("inv.items.previous")}</button>
          <span aria-live="polite">{t("inv.items.paging", { from: String(total === 0 ? 0 : offset + 1), to: String(offset + items.length), total: String(total) })}</span>
          <button type="button" disabled={offset + PAGE >= total} onClick={() => setOffset(offset + PAGE)}>{t("inv.items.next")}</button>
        </div>
      </Card>
      {confirm && <ConfirmDialog title={confirm.isActive ? t("inv.deactivate_title") : t("inv.activate_title")} message={t(confirm.isActive ? "inv.items.deactivate_message" : "inv.items.activate_message", { code: confirm.code })}
        confirmLabel={t("form.confirm")} onCancel={() => setConfirm(null)} onConfirm={() => void toggle(confirm)} />}
    </section>
  );
}

function ItemEditor({ editor, groups, units, caps, onClose, onSaved }: {
  editor: Editor; groups: InventoryGroup[]; units: InventoryUnit[]; caps: InventoryCapabilities; onClose: () => void; onSaved: (m: string) => void;
}) {
  const { t, locale } = useI18n();
  const auth = useAuth();
  const isNew = editor.mode === "new";
  const [item, setItem] = useState<InventoryItem | null>(editor.mode === "edit" ? editor.item : null);
  const canEdit = isNew ? caps.itemsCreate : caps.itemsUpdate;
  const [base, setBase] = useState(() => snapshot(toForm(item)));
  const [form, setForm] = useState<Form>(() => toForm(item));
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [error, setError] = useState<string | null>(null);
  const [conflict, setConflict] = useState(false);
  const [busy, setBusy] = useState(false);
  const [tab, setTab] = useState("general");
  const dirty = snapshot(form) !== base;
  useReportUnsaved("item-form", dirty, t("inv.item.form_section"));

  const unitById = useMemo(() => new Map(units.map((u) => [u.id, u])), [units]);
  const group = groups.find((g) => g.id === form.groupId);
  const goods = form.itemType === 1;
  const set = <K extends keyof Form>(k: K, v: Form[K]) => setForm((f) => ({ ...f, [k]: v }));
  const inputOf = (k: keyof Form) => (e: { target: { value: string } }) => set(k, e.target.value as never);
  const known = new Set([form.baseUnitId, ...form.alts.map((a) => a.unitId)].filter(Boolean));
  const unitChoices = (current: string) => units.filter((u) => u.isActive || u.id === current || known.has(u.id));

  function validate(): Record<string, string> {
    const v: Record<string, string> = {};
    if (form.code.trim() && !CODE_SHAPE.test(form.code.trim().toUpperCase())) v.code = t("field.invalid_format");
    if (!form.nameAr.trim() && !form.nameEn.trim()) { v.nameAr = t("inv.name_required"); v.nameEn = t("inv.name_required"); }
    if (!form.groupId) v.groupId = t("field.required");
    if (!form.baseUnitId) v.baseUnitId = t("field.required");
    const seen = new Set<string>();
    form.alts.forEach((a, i) => {
      const key = `alternateUnits[${i}]`;
      if (!a.unitId) v[`${key}.unitId`] = t("field.required");
      else if (a.unitId === form.baseUnitId) v[`${key}.unitId`] = t("inv.field.equals_base_unit");
      else if (seen.has(a.unitId)) v[`${key}.unitId`] = t("inv.field.duplicate");
      seen.add(a.unitId);
      const n = Number(a.factor);
      if (!a.factor.trim() || !Number.isFinite(n) || n <= 0 || !/^\d+(\.\d{1,10})?$/.test(a.factor.trim())) v[`${key}.factor`] = t("inv.field.invalid_factor");
    });
    for (const k of ["purchaseUnitId", "salesUnitId"] as const) if (form[k] && form[k] !== form.baseUnitId && !form.alts.some((a) => a.unitId === form[k])) v[k] = t("inv.field.unit_not_defined_for_item");
    return v;
  }

  const tabOfErrors = (e: Record<string, string>) => new Set(Object.keys(e).map(TAB_OF_FIELD));

  async function submit(e: FormEvent) {
    e.preventDefault();
    if (!canEdit) return;
    const v = validate();
    setErrors(v);
    if (Object.keys(v).length > 0) {
      setError(t("form.fix_errors"));
      const first = (["general", "units", "tracking"] as const).find((id) => tabOfErrors(v).has(id));
      if (first) setTab(first);
      return;
    }
    setBusy(true); setError(null); setConflict(false);
    try {
      const body: InventoryItemInput = {
        code: isNew ? blankToNull(form.code.trim().toUpperCase()) : undefined, nameAr: blankToNull(form.nameAr), nameEn: blankToNull(form.nameEn), itemType: form.itemType, groupId: form.groupId, baseUnitId: form.baseUnitId,
        purchaseUnitId: form.purchaseUnitId || null, salesUnitId: form.salesUnitId || null, isLot: goods && form.isLot, isSerial: goods && form.isSerial, hasExpiry: goods && form.hasExpiry, notes: blankToNull(form.notes),
        alternateUnits: form.alts.map((a) => ({ unitId: a.unitId, factor: Number(a.factor) })),
      };
      if (editor.mode === "new") { await auth.api.createInventoryItem(body); onSaved(t("inv.created")); }
      else { await auth.api.updateInventoryItem(item!.id, body, item!.revision); onSaved(t("inv.updated")); }
    } catch (err) {
      // A failed save changes nothing: the form keeps everything the user typed, on every tab.
      if (auth.handleError(err)) return;
      if (err instanceof ApiError && err.status === 412) setConflict(true);
      else if (err instanceof ApiError && err.status === 400) {
        const fe = invFieldErrors(t, err);
        setErrors(fe); setError(t("form.fix_errors"));
        const first = (["general", "units", "tracking"] as const).find((id) => tabOfErrors(fe).has(id));
        if (first) setTab(first);
      } else if (err instanceof ApiError && err.code === "item_code_taken") { setErrors({ code: t("inv.error.item_code_taken") }); setTab("general"); setError(t("form.fix_errors")); }
      else setError(errorText(t, err, "inv.error."));
    } finally { setBusy(false); }
  }

  async function reloadLatest() {
    if (!item) return;
    try {
      const latest = await auth.api.getInventoryItem(item.id);
      const next = toForm(latest);
      setItem(latest); setBase(snapshot(next)); setForm(next); setErrors({}); setConflict(false);
    } catch (err) { if (!auth.handleError(err)) setError(errorText(t, err, "inv.error.")); }
  }

  const invalid = tabOfErrors(errors);
  const methodText = (m: ValuationMethodCode | null | undefined) => (m === 1 ? t("inv.valuation.fifo") : m === 2 ? t("inv.valuation.average") : t("inv.valuation.undecided"));
  const altUnits = form.alts.map((a) => unitById.get(a.unitId)).filter((u): u is InventoryUnit => !!u);
  const defaultUnitChoices = [unitById.get(form.baseUnitId), ...altUnits].filter((u): u is InventoryUnit => !!u);

  const general = (
    <div className="fields">
      <Field id="it-code" label={t("inv.code")} error={errors.code} hint={isNew ? t("inv.items.code_auto_hint") : t("inv.items.code_fixed_hint")}>
        {(aria) => <input {...aria} name="code" value={form.code} onChange={inputOf("code")} readOnly={!canEdit || !isNew} dir="ltr" autoComplete="off" placeholder={isNew ? t("inv.items.code_auto") : undefined} />}
      </Field>
      <Field id="it-nameAr" label={t("inv.name_ar")} error={errors.nameAr}>{(aria) => <input {...aria} name="nameAr" value={form.nameAr} onChange={inputOf("nameAr")} readOnly={!canEdit} dir="rtl" autoComplete="off" />}</Field>
      <Field id="it-nameEn" label={t("inv.name_en")} error={errors.nameEn}>{(aria) => <input {...aria} name="nameEn" value={form.nameEn} onChange={inputOf("nameEn")} readOnly={!canEdit} dir="ltr" autoComplete="off" />}</Field>
      <Field id="it-type" label={t("inv.items.type")} error={errors.itemType} hint={isNew ? t("inv.items.type_hint") : t("inv.items.type_fixed_hint")}>
        {(aria) => (
          <select {...aria} name="itemType" value={String(form.itemType)} disabled={!canEdit || !isNew} onChange={(e) => set("itemType", Number(e.target.value) as ItemTypeCode)}>
            <option value="1">{t("inv.items.goods")}</option><option value="2">{t("inv.items.service")}</option>
          </select>
        )}
      </Field>
      <Field id="it-group" label={t("inv.items.group")} error={errors.groupId} required>
        {(aria) => (
          <select {...aria} name="groupId" value={form.groupId} disabled={!canEdit} onChange={inputOf("groupId")}>
            <option value="">{t("form.select")}</option>
            {groupTree(groups.filter((g) => g.isActive || g.id === form.groupId)).map(({ group: g, depth }) => <option key={g.id} value={g.id}>{"  ".repeat(depth)}{g.code} — {invName(g, locale)}</option>)}
          </select>
        )}
      </Field>
      {goods && <Field id="it-valuation" label={t("inv.groups.valuation")} hint={t("inv.items.valuation_hint")}>{(aria) => <input {...aria} readOnly value={group ? methodText(group.effectiveValuation) : "—"} />}</Field>}
      <Field id="it-notes" label={t("inv.items.notes")} error={errors.notes}>{(aria) => <textarea {...aria} name="notes" value={form.notes} onChange={inputOf("notes")} readOnly={!canEdit} rows={3} maxLength={1000} />}</Field>
    </div>
  );

  const unitsPanel = (
    <div>
      <div className="fields">
        <Field id="it-base" label={t("inv.items.base_unit")} error={errors.baseUnitId} required hint={isNew ? t("inv.items.base_unit_hint") : t("inv.items.base_unit_fixed_hint")}>
          {(aria) => (
            <select {...aria} name="baseUnitId" value={form.baseUnitId} disabled={!canEdit || !isNew} onChange={inputOf("baseUnitId")}>
              <option value="">{t("form.select")}</option>
              {unitChoices(form.baseUnitId).map((u) => <option key={u.id} value={u.id}>{unitLabel(u, locale)}</option>)}
            </select>
          )}
        </Field>
      </div>
      <h3 className="card-title">{t("inv.item.alternates")}</h3>
      <p className="hint">{t("inv.item.alternates_hint")}</p>
      <div className="table-wrap">
        <table className="data-table">
          <caption className="visually-hidden">{t("inv.item.alternates")}</caption>
          <thead><tr><th scope="col">{t("inv.item.alt_unit")}</th><th scope="col">{t("inv.item.alt_factor")}</th><th scope="col">{t("inv.actions")}</th></tr></thead>
          <tbody>
            {form.alts.length === 0 && <tr><td colSpan={3}>{t("inv.item.no_alternates")}</td></tr>}
            {form.alts.map((a, i) => (
              <tr key={a.key} data-testid={`alt-${i}`}>
                <td>
                  <Field id={`alt-unit-${i}`} label={t("inv.item.alt_unit")} error={errors[`alternateUnits[${i}].unitId`]}>
                    {(aria) => (
                      <select {...aria} value={a.unitId} disabled={!canEdit} onChange={(e) => set("alts", form.alts.map((r) => (r.key === a.key ? { ...r, unitId: e.target.value } : r)))}>
                        <option value="">{t("form.select")}</option>
                        {unitChoices(a.unitId).filter((u) => u.id !== form.baseUnitId).map((u) => <option key={u.id} value={u.id}>{unitLabel(u, locale)}</option>)}
                      </select>
                    )}
                  </Field>
                </td>
                <td>
                  <Field id={`alt-factor-${i}`} label={t("inv.item.alt_factor")} error={errors[`alternateUnits[${i}].factor`]} hint={t("inv.item.alt_factor_hint", { base: unitById.get(form.baseUnitId)?.code ?? "…" })}>
                    {(aria) => <input {...aria} value={a.factor} inputMode="decimal" dir="ltr" readOnly={!canEdit} onChange={(e) => set("alts", form.alts.map((r) => (r.key === a.key ? { ...r, factor: e.target.value } : r)))} />}
                  </Field>
                </td>
                <td>{canEdit && <button type="button" onClick={() => set("alts", form.alts.filter((r) => r.key !== a.key))}>{t("inv.item.remove")}</button>}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {canEdit && <button type="button" onClick={() => set("alts", [...form.alts, { key: ++rowKey, unitId: "", factor: "" }])}>{t("inv.item.add_alternate")}</button>}
      <div className="fields">
        <Field id="it-purchase" label={t("inv.item.purchase_unit")} error={errors.purchaseUnitId}>
          {(aria) => (
            <select {...aria} value={form.purchaseUnitId} disabled={!canEdit} onChange={inputOf("purchaseUnitId")}>
              <option value="">{t("inv.item.default_none")}</option>
              {defaultUnitChoices.map((u) => <option key={u.id} value={u.id}>{unitLabel(u, locale)}</option>)}
            </select>
          )}
        </Field>
        <Field id="it-sales" label={t("inv.item.sales_unit")} error={errors.salesUnitId}>
          {(aria) => (
            <select {...aria} value={form.salesUnitId} disabled={!canEdit} onChange={inputOf("salesUnitId")}>
              <option value="">{t("inv.item.default_none")}</option>
              {defaultUnitChoices.map((u) => <option key={u.id} value={u.id}>{unitLabel(u, locale)}</option>)}
            </select>
          )}
        </Field>
      </div>
    </div>
  );

  const tracking = (
    <fieldset className="fields" disabled={!canEdit}>
      <legend className="card-title">{t("inv.item.tracking")}</legend>
      <p className="hint">{t("inv.item.tracking_hint")}</p>
      {([["isLot", "inv.item.lot"], ["isSerial", "inv.item.serial"], ["hasExpiry", "inv.item.expiry"]] as const).map(([k, label]) => (
        <label key={k} className="checkbox"><input type="checkbox" checked={form[k]} onChange={(e) => set(k, e.target.checked)} /> {t(label)}</label>
      ))}
    </fieldset>
  );

  const tabs: TabItem[] = [
    { id: "general", label: t("inv.item.tab.general"), content: general, invalid: invalid.has("general") },
    { id: "units", label: t("inv.item.tab.units"), content: unitsPanel, invalid: invalid.has("units") },
    ...(goods ? [{ id: "tracking", label: t("inv.item.tab.tracking"), content: tracking, invalid: invalid.has("tracking") } as TabItem] : []),
    ...(item && auth.can("company.tax.view") ? [{ id: "tax", label: t("tax.tab.class"), content: <TaxClassPicker kind="item" subjectId={item.id} /> } as TabItem] : []),
    ...(item ? [{ id: "barcodes", label: t("inv.item.tab.barcodes"), content: <BarcodesPanel item={item} units={units} canAdd={caps.barcodesCreate} canActivate={caps.barcodesActivate} /> } as TabItem] : []),
  ];
  const activeTab = tabs.some((x) => x.id === tab) ? tab : "general";

  return (
    <section aria-labelledby="page-title" data-testid="item-editor">
      <PageHeader title={isNew ? t("inv.item.new_title") : item!.code} description={isNew ? t("inv.item.new_subtitle") : invName(item!, locale)}
        actions={item && <span className={`chip ${item.isActive ? "chip-ok" : "chip-off"}`}>{item.isActive ? t("inv.active") : t("inv.inactive")}</span>} />
      {!canEdit && <Banner kind="info" testId="form-read-only">{t("form.read_only")}</Banner>}
      {conflict && <Banner kind="error" testId="form-conflict">{t("inv.error.revision_conflict")} <button type="button" onClick={() => void reloadLatest()}>{t("form.reload_latest")}</button></Banner>}
      {error && <Banner kind="error" testId="form-error">{error}</Banner>}
      <form onSubmit={submit} noValidate>
        <Tabs label={t("inv.item.tabs_label")} tabs={tabs} value={activeTab} onChange={setTab} testId="item-tabs" />
        <div className="actions" data-testid="item-actions">
          <button type="button" onClick={onClose}>{t("inv.cancel")}</button>
          {canEdit && <button type="submit" className="primary" disabled={busy}>{busy ? t("loading") : t("inv.save")}</button>}
        </div>
      </form>
    </section>
  );
}

/** Barcodes of a saved item. Each add / activate / deactivate is its own audited operation applied immediately; it never touches the unsaved edits on the other tabs. */
function BarcodesPanel({ item, units, canAdd, canActivate }: { item: InventoryItem; units: InventoryUnit[]; canAdd: boolean; canActivate: boolean }) {
  const { t, locale } = useI18n();
  const tRef = useRef(t);
  tRef.current = t;
  const auth = useAuth();
  const [list, setList] = useState<InventoryBarcode[] | null>(null);
  const [value, setValue] = useState("");
  const [unitId, setUnitId] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [fieldError, setFieldError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const unitById = new Map(units.map((u) => [u.id, u]));
  const available = [unitById.get(item.baseUnitId), ...item.alternateUnits.map((a) => unitById.get(a.unitId))].filter((u): u is InventoryUnit => !!u);

  const load = useCallback(async () => {
    try { setList((await auth.api.listItemBarcodes(item.id)).barcodes); }
    catch (e) { if (!auth.handleError(e)) setError(errorText(tRef.current, e, "inv.error.")); }
  }, [auth.api, auth.handleError, item.id]);
  useEffect(() => { void load(); }, [load]);

  async function add() {
    if (!value.trim()) { setFieldError(t("field.required")); return; }
    setBusy(true); setError(null); setFieldError(null);
    try { await auth.api.addItemBarcode(item.id, { barcode: value.trim(), unitId: unitId || null }); setValue(""); await load(); }
    catch (err) {
      if (auth.handleError(err)) return;
      if (err instanceof ApiError && err.status === 400) setFieldError(Object.values(invFieldErrors(t, err))[0] ?? t("field.invalid"));
      else if (err instanceof ApiError && err.code === "barcode_taken") setFieldError(t("inv.error.barcode_taken"));
      else setError(errorText(t, err, "inv.error."));
    } finally { setBusy(false); }
  }

  async function toggle(b: InventoryBarcode) {
    setBusy(true); setError(null);
    try { await (b.isActive ? auth.api.deactivateItemBarcode(item.id, b.id) : auth.api.activateItemBarcode(item.id, b.id)); await load(); }
    catch (err) { if (!auth.handleError(err)) setError(errorText(t, err, "inv.error.")); }
    finally { setBusy(false); }
  }

  const kind = (k: number) => t(`inv.barcode.kind.${k}`);
  return (
    <div data-testid="barcodes-panel">
      <p className="hint">{t("inv.barcode.hint")}</p>
      {error && <Banner kind="error" testId="barcode-error">{error}</Banner>}
      {canAdd && item.isActive && (
        <div className="fields">
          <Field id="bc-value" label={t("inv.barcode.value")} error={fieldError ?? undefined} hint={t("inv.barcode.value_hint")}>
            {(aria) => <input {...aria} value={value} dir="ltr" autoComplete="off" onChange={(e) => setValue(e.target.value)} onKeyDown={(e) => { if (e.key === "Enter") { e.preventDefault(); void add(); } }} />}
          </Field>
          <Field id="bc-unit" label={t("inv.barcode.unit")}>
            {(aria) => (
              <select {...aria} value={unitId} onChange={(e) => setUnitId(e.target.value)}>
                <option value="">{t("inv.barcode.base_unit")}</option>
                {available.filter((u) => u.id !== item.baseUnitId).map((u) => <option key={u.id} value={u.id}>{unitLabel(u, locale)}</option>)}
              </select>
            )}
          </Field>
          <div><button type="button" className="primary" disabled={busy} onClick={() => void add()}>{t("inv.barcode.add")}</button></div>
        </div>
      )}
      <div className="table-wrap">
        <table className="data-table">
          <caption className="visually-hidden">{t("inv.item.tab.barcodes")}</caption>
          <thead><tr><th scope="col">{t("inv.barcode.value")}</th><th scope="col">{t("inv.barcode.kind")}</th><th scope="col">{t("inv.barcode.unit")}</th><th scope="col">{t("inv.status")}</th><th scope="col">{t("inv.actions")}</th></tr></thead>
          <tbody>
            {list === null && <tr><td colSpan={5}>{t("loading")}</td></tr>}
            {list?.length === 0 && <tr><td colSpan={5}>{t("inv.barcode.none")}</td></tr>}
            {list?.map((b) => (
              <tr key={b.id} data-testid={`barcode-${b.barcode}`}>
                <td><bdi dir="ltr">{b.barcode}</bdi></td><td>{kind(b.kind)}</td><td>{unitById.get(b.unitId)?.code ?? "—"}</td>
                <td><span className={`chip ${b.isActive ? "chip-ok" : "chip-off"}`}>{b.isActive ? t("inv.active") : t("inv.inactive")}</span></td>
                <td>{canActivate && (b.isActive || item.isActive) && <button type="button" disabled={busy} onClick={() => void toggle(b)}>{b.isActive ? t("inv.deactivate") : t("inv.activate")}</button>}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}
