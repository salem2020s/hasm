import { ApiError, type Warehouse, type WarehouseBranchRef, type WarehouseInput, type WarehousesResponse } from "@hasm/api-client";
import { useI18n } from "@hasm/ui";
import { useCallback, useEffect, useMemo, useRef, useState, type FormEvent } from "react";
import { useAuth } from "../auth";
import { Banner, Card, ConfirmDialog, errorText, Field, fieldErrors, Loading, PageHeader } from "../components";
import { useReportUnsaved, useUnsaved } from "../unsaved";
import { branchName } from "./Branches";

/**
 * Warehouse management (Phase 3.1C). ONE implementation for the Web app and the Windows Desktop app. All rules (the branch association, the
 * Trial limit, immutable code and branch, the active-branch dependency, optimistic concurrency, warehouse-scoped RBAC, auditing) are enforced by
 * the server and PostgreSQL; this screen validates for convenience only and shows or hides controls from what the server reports the caller may do.
 * Stock, movements and valuation are NOT part of this screen (later inventory phases).
 */

const CODE_SHAPE = /^[A-Z0-9][A-Z0-9_-]{0,19}$/;
const PHONE_SHAPE = /^[\p{Nd}+()\-. ]{5,30}$/u;
const EMAIL_SHAPE = /^[^@\s]+@[^@\s]+\.[^@\s]+$/;
const URL_SHAPE = /^https?:\/\/\S+$/i;

type FieldKey = "code" | "nameAr" | "nameEn" | "addressLine1" | "addressLine2" | "city" | "region" | "postalCode" | "phone" | "email" | "website";
type Form = Record<FieldKey, string>;
const KEYS: FieldKey[] = ["code", "nameAr", "nameEn", "addressLine1", "addressLine2", "city", "region", "postalCode", "phone", "email", "website"];
const empty = (): Form => Object.fromEntries(KEYS.map((k) => [k, ""])) as Form;
const toForm = (w: Warehouse | null): Form => (w ? (Object.fromEntries(KEYS.map((k) => [k, w[k] ?? ""])) as Form) : empty());
const blankToNull = (s: string) => (s.trim() ? s.trim() : null);

/** The warehouse name for the current language, falling back to the other language (same convention as branches and company names). */
export function warehouseName(w: Pick<Warehouse, "nameAr" | "nameEn" | "code">, locale: string): string {
  const preferred = locale === "en" ? w.nameEn : w.nameAr;
  const other = locale === "en" ? w.nameAr : w.nameEn;
  return preferred?.trim() || other?.trim() || w.code;
}

type Editor = { mode: "new" } | { mode: "edit"; warehouse: Warehouse };
type Confirm = { kind: "activate" | "deactivate"; warehouse: Warehouse };

export function WarehousesPage() {
  const { t, locale } = useI18n();
  const tRef = useRef(t);
  tRef.current = t;
  const auth = useAuth();
  const { guard } = useUnsaved();
  const [data, setData] = useState<WarehousesResponse | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [search, setSearch] = useState("");
  const [status, setStatus] = useState<"all" | "active" | "inactive">("all");
  const [editor, setEditor] = useState<Editor | null>(null);
  const [confirm, setConfirm] = useState<Confirm | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [conflict, setConflict] = useState(false);
  const [busy, setBusy] = useState(false);

  const load = useCallback(async () => {
    try {
      setData(await auth.api.listWarehouses());
      setLoadError(null);
    } catch (e) {
      if (!auth.handleError(e)) setLoadError(errorText(tRef.current, e, "warehouses.error."));
    }
  }, [auth.api, auth.handleError]);
  useEffect(() => { void load(); }, [load]);

  const branchOf = useMemo(() => new Map((data?.branches ?? []).map((b) => [b.id, b])), [data]);
  const branchLabel = (id: string) => { const b = branchOf.get(id); return b ? `${b.code} — ${branchName(b, locale)}` : "—"; };

  const visible = useMemo(() => {
    const q = search.trim().toLowerCase();
    return (data?.warehouses ?? []).filter((w) =>
      (status === "all" || (status === "active") === w.isActive)
      && (!q || w.code.toLowerCase().includes(q) || (w.nameAr ?? "").toLowerCase().includes(q) || (w.nameEn ?? "").toLowerCase().includes(q)));
  }, [data, search, status]);

  if (loadError) return <Banner kind="error">{loadError}</Banner>;
  if (!data) return <Loading />;

  const can = data.can;
  const limitReached = data.limit !== null && data.everCreated !== null && data.everCreated >= data.limit;
  const anyAction = can.create || can.activate || can.update.length > 0;
  const activeBranches = data.branches.filter((b) => b.isActive);

  function openEditor(next: Editor) { guard(() => { setNotice(null); setError(null); setConflict(false); setEditor(next); }); }
  function closeEditor() { guard(() => setEditor(null)); }

  async function run(action: () => Promise<unknown>, message: string) {
    setBusy(true);
    setError(null);
    setConflict(false);
    setNotice(null);
    try {
      await action();
      await load();
      setNotice(message);
    } catch (err) {
      if (auth.handleError(err)) return;
      if (err instanceof ApiError && err.status === 412) setConflict(true);
      else setError(errorText(t, err, "warehouses.error."));
    } finally {
      setBusy(false);
    }
  }

  const apply = (c: Confirm) => {
    setConfirm(null);
    if (c.kind === "activate") void run(() => auth.api.activateWarehouse(c.warehouse.id, c.warehouse.revision), t("warehouses.changed"));
    else void run(() => auth.api.deactivateWarehouse(c.warehouse.id, c.warehouse.revision), t("warehouses.changed"));
  };

  return (
    <section aria-labelledby="page-title">
      <PageHeader
        title={t("warehouses.title")}
        description={t("warehouses.subtitle")}
        actions={can.create && <button type="button" className="primary" disabled={limitReached || busy || activeBranches.length === 0} onClick={() => openEditor({ mode: "new" })}>{t("warehouses.new")}</button>}
      />
      {!anyAction && <Banner kind="info" testId="read-only">{t("form.read_only")}</Banner>}
      {data.limit !== null && data.everCreated !== null && (
        <div className="notices">
          <Banner kind="info" testId="warehouse-usage">{t("warehouses.usage", { created: String(data.everCreated), limit: String(data.limit) })}</Banner>
          {limitReached && <Banner kind="warning" testId="warehouse-limit">{t("warehouses.limit_reached")}</Banner>}
        </div>
      )}
      {conflict && (
        <Banner kind="error" testId="conflict">
          {t("warehouses.error.revision_conflict")} <button type="button" onClick={() => { setConflict(false); void load(); }}>{t("form.reload_latest")}</button>
        </Banner>
      )}
      {error && <Banner kind="error">{error}</Banner>}
      {notice && <Banner kind="success" testId="saved">{notice}</Banner>}

      <Card title={t("warehouses.list")}>
        <div className="toolbar">
          <Field id="w-search" label={t("warehouses.search")}>
            {(aria) => <input {...aria} type="search" value={search} onChange={(e) => setSearch(e.target.value)} autoComplete="off" />}
          </Field>
          <Field id="w-status" label={t("warehouses.filter_status")}>
            {(aria) => (
              <select {...aria} value={status} onChange={(e) => setStatus(e.target.value as typeof status)}>
                <option value="all">{t("warehouses.filter_all")}</option>
                <option value="active">{t("warehouses.filter_active")}</option>
                <option value="inactive">{t("warehouses.filter_inactive")}</option>
              </select>
            )}
          </Field>
        </div>
        <div className="table-wrap">
          <table className="data-table">
            <caption className="visually-hidden">{t("warehouses.list")}</caption>
            <thead>
              <tr>
                <th scope="col">{t("warehouses.code")}</th><th scope="col">{t("warehouses.name")}</th><th scope="col">{t("warehouses.branch")}</th>
                <th scope="col">{t("warehouses.status")}</th><th scope="col">{t("warehouses.actions")}</th>
              </tr>
            </thead>
            <tbody>
              {visible.length === 0 && <tr><td colSpan={5}>{data.warehouses.length === 0 ? t("warehouses.none") : t("warehouses.none_filtered")}</td></tr>}
              {visible.map((w) => {
                const canUpdate = can.update.includes(w.id);
                return (
                  <tr key={w.id} data-testid={`warehouse-${w.code}`}>
                    <td><bdi dir="ltr">{w.code}</bdi></td>
                    <td><bdi>{warehouseName(w, locale)}</bdi></td>
                    <td><bdi>{branchLabel(w.branchId)}</bdi></td>
                    <td><span className={`chip ${w.isActive ? "chip-ok" : "chip-off"}`}>{w.isActive ? t("warehouses.active") : t("warehouses.inactive")}</span></td>
                    <td>
                      <button type="button" disabled={busy} onClick={() => openEditor({ mode: "edit", warehouse: w })}>{canUpdate ? t("warehouses.edit") : t("warehouses.view")}</button>{" "}
                      {can.activate && w.isActive && <button type="button" disabled={busy} onClick={() => setConfirm({ kind: "deactivate", warehouse: w })}>{t("warehouses.deactivate")}</button>}
                      {can.activate && !w.isActive && <button type="button" disabled={busy} onClick={() => setConfirm({ kind: "activate", warehouse: w })}>{t("warehouses.activate")}</button>}
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      </Card>

      {editor && (
        <WarehouseForm
          key={editor.mode === "edit" ? editor.warehouse.id : "new"}
          editor={editor}
          branches={activeBranches}
          branchLabel={branchLabel}
          canEdit={editor.mode === "new" ? can.create : can.update.includes(editor.warehouse.id)}
          onClose={closeEditor}
          onSaved={(message) => { setEditor(null); setNotice(message); void load(); }}
        />
      )}

      {confirm && (
        <ConfirmDialog
          title={confirm.kind === "activate" ? t("warehouses.activate_title") : t("warehouses.deactivate_title")}
          message={confirm.kind === "activate" ? t("warehouses.activate_message", { code: confirm.warehouse.code }) : t("warehouses.deactivate_message", { code: confirm.warehouse.code })}
          confirmLabel={t("form.confirm")}
          onCancel={() => setConfirm(null)}
          onConfirm={() => apply(confirm)}
        />
      )}
    </section>
  );
}

function WarehouseForm({ editor, branches, branchLabel, canEdit, onClose, onSaved }: {
  editor: Editor; branches: WarehouseBranchRef[]; branchLabel: (id: string) => string; canEdit: boolean; onClose: () => void; onSaved: (message: string) => void;
}) {
  const { t, locale } = useI18n();
  const auth = useAuth();
  const isNew = editor.mode === "new";
  const [revision, setRevision] = useState(editor.mode === "edit" ? editor.warehouse.revision : 0);
  const [baseline, setBaseline] = useState<Form>(() => toForm(editor.mode === "edit" ? editor.warehouse : null));
  const [form, setForm] = useState<Form>(baseline);
  const [branchId, setBranchId] = useState("");
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [error, setError] = useState<string | null>(null);
  const [conflict, setConflict] = useState(false);
  const [busy, setBusy] = useState(false);

  const dirty = KEYS.some((k) => form[k] !== baseline[k]) || (isNew && branchId !== "");
  useReportUnsaved("warehouse-form", dirty, t("warehouses.form_section"));

  const set = (k: FieldKey) => (e: { target: { value: string } }) => setForm((f) => ({ ...f, [k]: e.target.value }));

  function validate(): Record<string, string> {
    const v: Record<string, string> = {};
    if (isNew) {
      const code = form.code.trim().toUpperCase();
      if (!code) v.code = t("field.required"); else if (!CODE_SHAPE.test(code)) v.code = t("field.invalid_format");
      if (!branchId) v.branchId = t("warehouses.branch_required");
    }
    if (!form.nameAr.trim() && !form.nameEn.trim()) { v.nameAr = t("warehouses.name_required"); v.nameEn = t("warehouses.name_required"); }
    if (form.phone.trim() && !PHONE_SHAPE.test(form.phone.trim())) v.phone = t("field.invalid_format");
    if (form.email.trim() && !EMAIL_SHAPE.test(form.email.trim())) v.email = t("field.invalid_format");
    if (form.website.trim() && !URL_SHAPE.test(form.website.trim())) v.website = t("field.invalid_url");
    return v;
  }

  function serverFieldErrors(err: unknown): Record<string, string> {
    const out = fieldErrors(t, err);
    if (err instanceof ApiError) for (const f of err.details.fields ?? []) {
      if (f.code === "name_required") out[f.field] = t("warehouses.name_required");
      if (f.field === "branchId") out.branchId = f.code === "required" ? t("warehouses.branch_required") : t("warehouses.error.branch_inactive");
    }
    return out;
  }

  async function submit(e: FormEvent) {
    e.preventDefault();
    if (!canEdit) return;
    const v = validate();
    setErrors(v);
    if (Object.keys(v).length > 0) { setError(t("form.fix_errors")); return; }
    setBusy(true);
    setError(null);
    setConflict(false);
    try {
      const body: WarehouseInput = {
        nameAr: blankToNull(form.nameAr), nameEn: blankToNull(form.nameEn),
        addressLine1: blankToNull(form.addressLine1), addressLine2: blankToNull(form.addressLine2), city: blankToNull(form.city), region: blankToNull(form.region),
        postalCode: blankToNull(form.postalCode), phone: blankToNull(form.phone), email: blankToNull(form.email), website: blankToNull(form.website),
      };
      if (editor.mode === "new") {
        await auth.api.createWarehouse({ ...body, code: form.code.trim().toUpperCase(), branchId });
        onSaved(t("warehouses.created"));
      } else {
        await auth.api.updateWarehouse(editor.warehouse.id, body, revision);
        onSaved(t("warehouses.updated"));
      }
    } catch (err) {
      // A failed save changes nothing: the form keeps what the user typed.
      if (auth.handleError(err)) return;
      if (err instanceof ApiError && err.status === 412) setConflict(true);
      else if (err instanceof ApiError && err.status === 400) { setErrors(serverFieldErrors(err)); setError(t("form.fix_errors")); }
      else if (err instanceof ApiError && err.status === 409 && err.code === "warehouse_code_taken") { setErrors({ code: t("warehouses.error.warehouse_code_taken") }); setError(t("form.fix_errors")); }
      else setError(errorText(t, err, "warehouses.error."));
    } finally {
      setBusy(false);
    }
  }

  async function reloadLatest() {
    if (editor.mode !== "edit") return;
    try {
      const latest = await auth.api.getWarehouse(editor.warehouse.id);
      const next = toForm(latest);
      setRevision(latest.revision);
      setBaseline(next);
      setForm(next);
      setErrors({});
      setConflict(false);
    } catch (err) {
      if (!auth.handleError(err)) setError(errorText(t, err, "warehouses.error."));
    }
  }

  const text = (k: FieldKey, label: string, extra: { required?: boolean; dir?: "ltr" | "rtl"; type?: string; hint?: string; readOnly?: boolean } = {}) => (
    <Field id={`w-${k}`} label={label} error={errors[k]} required={extra.required} hint={extra.hint}>
      {(aria) => <input {...aria} name={k} value={form[k]} onChange={set(k)} readOnly={!canEdit || extra.readOnly} dir={extra.dir} type={extra.type ?? "text"} autoComplete="off" />}
    </Field>
  );

  return (
    <Card title={isNew ? t("warehouses.form_new") : t("warehouses.form_edit")}>
      {!canEdit && <Banner kind="info" testId="form-read-only">{t("form.read_only")}</Banner>}
      {conflict && (
        <Banner kind="error" testId="form-conflict">
          {t("warehouses.error.revision_conflict")} <button type="button" onClick={() => void reloadLatest()}>{t("form.reload_latest")}</button>
        </Banner>
      )}
      {error && <Banner kind="error" testId="form-error">{error}</Banner>}
      <form onSubmit={submit} noValidate className="form-grid">
        <Card title={t("warehouses.identity")} hint={t("warehouses.names_hint")}>
          <div className="fields">
            {text("code", t("warehouses.code"), { required: isNew, dir: "ltr", readOnly: !isNew, hint: isNew ? t("warehouses.code_hint") : undefined })}
            {isNew ? (
              <Field id="w-branchId" label={t("warehouses.branch_field")} error={errors.branchId} required hint={branches.length === 0 ? t("warehouses.no_branch") : t("warehouses.branch_hint")}>
                {(aria) => (
                  <select {...aria} name="branchId" value={branchId} onChange={(e) => setBranchId(e.target.value)} disabled={!canEdit}>
                    <option value="">{t("form.select")}</option>
                    {branches.map((b) => <option key={b.id} value={b.id}>{b.code} — {branchName(b, locale)}</option>)}
                  </select>
                )}
              </Field>
            ) : (
              <Field id="w-branchId" label={t("warehouses.branch_field")} hint={t("warehouses.branch_hint")}>
                {(aria) => <input {...aria} name="branchId" value={branchLabel(editor.warehouse.branchId)} readOnly />}
              </Field>
            )}
            {text("nameAr", t("warehouses.name_ar"), { dir: "rtl" })}
            {text("nameEn", t("warehouses.name_en"), { dir: "ltr" })}
          </div>
        </Card>
        <Card title={t("warehouses.address")}>
          <div className="fields">
            {text("addressLine1", t("warehouses.address1"))}
            {text("addressLine2", t("warehouses.address2"))}
            {text("city", t("warehouses.city"))}
            {text("region", t("warehouses.region"))}
            {text("postalCode", t("warehouses.postal_code"), { dir: "ltr" })}
          </div>
        </Card>
        <Card title={t("warehouses.contact")}>
          <div className="fields">
            {text("phone", t("warehouses.phone"), { dir: "ltr", type: "tel" })}
            {text("email", t("warehouses.email"), { dir: "ltr", type: "email" })}
            {text("website", t("warehouses.website"), { dir: "ltr", type: "url" })}
          </div>
        </Card>
        <div className="actions">
          <button type="button" onClick={onClose}>{t("warehouses.close_form")}</button>
          {canEdit && <button type="submit" className="primary" disabled={busy}>{busy ? t("loading") : t("warehouses.save")}</button>}
        </div>
      </form>
    </Card>
  );
}
