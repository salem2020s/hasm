import { ApiError, type InventoryCapabilities, type InventoryUnit } from "@hasm/api-client";
import { useI18n } from "@hasm/ui";
import { useCallback, useEffect, useMemo, useRef, useState, type FormEvent } from "react";
import { useAuth } from "../auth";
import { Banner, Card, ConfirmDialog, errorText, Field, Loading, PageHeader } from "../components";
import { navigate } from "../router";
import { useReportUnsaved, useUnsaved } from "../unsaved";
import { blankToNull, invFieldErrors, invName } from "./inventoryCommon";

/**
 * Units of measure (Phase 3.4). ONE implementation for Web and Windows Desktop. The code and the number of decimals are fixed for life; every rule (uniqueness, concurrency, audit, authorization)
 * is enforced by the server and PostgreSQL. This screen validates for convenience only. Simple screen: no tabs.
 */
const CODE_SHAPE = /^[A-Z0-9][A-Z0-9_-]{0,19}$/;
type Editor = { mode: "new" } | { mode: "edit"; unit: InventoryUnit };

export function InventoryUnitsPage() {
  const { t, locale } = useI18n();
  const tRef = useRef(t);
  tRef.current = t;
  const auth = useAuth();
  const { guard } = useUnsaved();
  const [units, setUnits] = useState<InventoryUnit[] | null>(null);
  const [caps, setCaps] = useState<InventoryCapabilities | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [editor, setEditor] = useState<Editor | null>(null);
  const [confirm, setConfirm] = useState<InventoryUnit | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [conflict, setConflict] = useState(false);
  const [busy, setBusy] = useState(false);

  const load = useCallback(async () => {
    try {
      const [u, c] = await Promise.all([auth.api.listInventoryUnits(), auth.api.getInventoryCapabilities()]);
      setUnits(u.units);
      setCaps(c);
      setLoadError(null);
    } catch (e) {
      if (!auth.handleError(e)) setLoadError(errorText(tRef.current, e, "inv.error."));
    }
  }, [auth.api, auth.handleError]);
  useEffect(() => { void load(); }, [load]);

  const sorted = useMemo(() => [...(units ?? [])].sort((a, b) => a.code.localeCompare(b.code)), [units]);
  if (loadError) return <Banner kind="error">{loadError}</Banner>;
  if (!units || !caps) return <Loading />;

  async function toggle(u: InventoryUnit) {
    setConfirm(null);
    setBusy(true); setError(null); setConflict(false); setNotice(null);
    try {
      await (u.isActive ? auth.api.deactivateInventoryUnit(u.id, u.revision) : auth.api.activateInventoryUnit(u.id, u.revision));
      await load();
      setNotice(t("inv.changed"));
    } catch (err) {
      if (auth.handleError(err)) return;
      if (err instanceof ApiError && err.status === 412) setConflict(true); else setError(errorText(t, err, "inv.error."));
    } finally { setBusy(false); }
  }

  return (
    <section aria-labelledby="page-title">
      <PageHeader title={t("inv.units.title")} description={t("inv.units.subtitle")}
        actions={<>{auth.can("company.imports.upload") && auth.can("company.units.create") && <button type="button" disabled={busy} onClick={() => guard(() => navigate("/company/imports"))}>{t("inv.import")}</button>}{caps.unitsCreate && <button type="button" className="primary" disabled={busy} onClick={() => guard(() => { setNotice(null); setEditor({ mode: "new" }); })}>{t("inv.units.new")}</button>}</>} />
      {!caps.unitsCreate && !caps.unitsUpdate && <Banner kind="info" testId="read-only">{t("form.read_only")}</Banner>}
      {conflict && <Banner kind="error" testId="conflict">{t("inv.error.revision_conflict")} <button type="button" onClick={() => { setConflict(false); void load(); }}>{t("form.reload_latest")}</button></Banner>}
      {error && <Banner kind="error">{error}</Banner>}
      {notice && <Banner kind="success" testId="saved">{notice}</Banner>}
      <Card title={t("inv.units.list")}>
        <div className="table-wrap">
          <table className="data-table">
            <caption className="visually-hidden">{t("inv.units.list")}</caption>
            <thead><tr><th scope="col">{t("inv.code")}</th><th scope="col">{t("inv.name")}</th><th scope="col">{t("inv.units.decimals")}</th><th scope="col">{t("inv.status")}</th><th scope="col">{t("inv.actions")}</th></tr></thead>
            <tbody>
              {sorted.length === 0 && <tr><td colSpan={5}>{t("inv.units.none")}</td></tr>}
              {sorted.map((u) => (
                <tr key={u.id} data-testid={`unit-${u.code}`}>
                  <td><bdi dir="ltr">{u.code}</bdi></td><td><bdi>{invName(u, locale)}</bdi></td><td className="num">{u.decimals}</td>
                  <td><span className={`chip ${u.isActive ? "chip-ok" : "chip-off"}`}>{u.isActive ? t("inv.active") : t("inv.inactive")}</span></td>
                  <td>
                    <button type="button" disabled={busy} onClick={() => guard(() => { setNotice(null); setEditor({ mode: "edit", unit: u }); })}>{caps.unitsUpdate ? t("inv.edit") : t("inv.view")}</button>{" "}
                    {caps.unitsActivate && <button type="button" disabled={busy} onClick={() => setConfirm(u)}>{u.isActive ? t("inv.deactivate") : t("inv.activate")}</button>}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </Card>
      {editor && <UnitForm key={editor.mode === "edit" ? editor.unit.id : "new"} editor={editor} canEdit={editor.mode === "new" ? caps.unitsCreate : caps.unitsUpdate}
        onClose={() => guard(() => setEditor(null))} onSaved={(m) => { setEditor(null); setNotice(m); void load(); }} />}
      {confirm && <ConfirmDialog title={confirm.isActive ? t("inv.deactivate_title") : t("inv.activate_title")} message={t(confirm.isActive ? "inv.units.deactivate_message" : "inv.units.activate_message", { code: confirm.code })}
        confirmLabel={t("form.confirm")} onCancel={() => setConfirm(null)} onConfirm={() => void toggle(confirm)} />}
    </section>
  );
}

function UnitForm({ editor, canEdit, onClose, onSaved }: { editor: Editor; canEdit: boolean; onClose: () => void; onSaved: (m: string) => void }) {
  const { t } = useI18n();
  const auth = useAuth();
  const isNew = editor.mode === "new";
  const initial = editor.mode === "edit" ? editor.unit : null;
  const [revision, setRevision] = useState(initial?.revision ?? 0);
  const [base, setBase] = useState({ code: initial?.code ?? "", nameAr: initial?.nameAr ?? "", nameEn: initial?.nameEn ?? "", decimals: String(initial?.decimals ?? 0) });
  const [form, setForm] = useState(base);
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [error, setError] = useState<string | null>(null);
  const [conflict, setConflict] = useState(false);
  const [busy, setBusy] = useState(false);
  const dirty = (Object.keys(form) as (keyof typeof form)[]).some((k) => form[k] !== base[k]);
  useReportUnsaved("unit-form", dirty, t("inv.units.form_section"));
  const set = (k: keyof typeof form) => (e: { target: { value: string } }) => setForm((f) => ({ ...f, [k]: e.target.value }));

  async function submit(e: FormEvent) {
    e.preventDefault();
    if (!canEdit) return;
    const v: Record<string, string> = {};
    if (isNew) {
      const code = form.code.trim().toUpperCase();
      if (!code) v.code = t("field.required"); else if (!CODE_SHAPE.test(code)) v.code = t("field.invalid_format");
      const d = Number(form.decimals);
      if (!Number.isInteger(d) || d < 0 || d > 6) v.decimals = t("inv.field.out_of_range");
    }
    if (!form.nameAr.trim() && !form.nameEn.trim()) { v.nameAr = t("inv.name_required"); v.nameEn = t("inv.name_required"); }
    setErrors(v);
    if (Object.keys(v).length > 0) { setError(t("form.fix_errors")); return; }
    setBusy(true); setError(null); setConflict(false);
    try {
      const body = { nameAr: blankToNull(form.nameAr), nameEn: blankToNull(form.nameEn), decimals: Number(form.decimals) };
      if (editor.mode === "new") { await auth.api.createInventoryUnit({ ...body, code: form.code.trim().toUpperCase() }); onSaved(t("inv.created")); }
      else { await auth.api.updateInventoryUnit(editor.unit.id, body, revision); onSaved(t("inv.updated")); }
    } catch (err) {
      if (auth.handleError(err)) return;
      if (err instanceof ApiError && err.status === 412) setConflict(true);
      else if (err instanceof ApiError && err.status === 400) { setErrors(invFieldErrors(t, err)); setError(t("form.fix_errors")); }
      else if (err instanceof ApiError && err.code === "unit_code_taken") { setErrors({ code: t("inv.error.unit_code_taken") }); setError(t("form.fix_errors")); }
      else setError(errorText(t, err, "inv.error."));
    } finally { setBusy(false); }
  }

  async function reloadLatest() {
    if (editor.mode !== "edit") return;
    try {
      const latest = (await auth.api.listInventoryUnits()).units.find((u) => u.id === editor.unit.id);
      if (!latest) return;
      const next = { code: latest.code, nameAr: latest.nameAr ?? "", nameEn: latest.nameEn ?? "", decimals: String(latest.decimals) };
      setRevision(latest.revision); setBase(next); setForm(next); setErrors({}); setConflict(false);
    } catch (err) { if (!auth.handleError(err)) setError(errorText(t, err, "inv.error.")); }
  }

  return (
    <Card title={isNew ? t("inv.units.form_new") : t("inv.units.form_edit", { code: base.code })}>
      {!canEdit && <Banner kind="info" testId="form-read-only">{t("form.read_only")}</Banner>}
      {conflict && <Banner kind="error" testId="form-conflict">{t("inv.error.revision_conflict")} <button type="button" onClick={() => void reloadLatest()}>{t("form.reload_latest")}</button></Banner>}
      {error && <Banner kind="error" testId="form-error">{error}</Banner>}
      <form onSubmit={submit} noValidate className="form-grid">
        <div className="fields">
          <Field id="u-code" label={t("inv.code")} error={errors.code} required={isNew} hint={isNew ? t("inv.code_hint") : t("inv.units.fixed_hint")}>
            {(aria) => <input {...aria} name="code" value={form.code} onChange={set("code")} readOnly={!canEdit || !isNew} dir="ltr" autoComplete="off" />}
          </Field>
          <Field id="u-nameAr" label={t("inv.name_ar")} error={errors.nameAr}>{(aria) => <input {...aria} name="nameAr" value={form.nameAr} onChange={set("nameAr")} readOnly={!canEdit} dir="rtl" autoComplete="off" />}</Field>
          <Field id="u-nameEn" label={t("inv.name_en")} error={errors.nameEn}>{(aria) => <input {...aria} name="nameEn" value={form.nameEn} onChange={set("nameEn")} readOnly={!canEdit} dir="ltr" autoComplete="off" />}</Field>
          <Field id="u-decimals" label={t("inv.units.decimals")} error={errors.decimals} required={isNew} hint={t("inv.units.decimals_hint")}>
            {(aria) => <input {...aria} name="decimals" type="number" min={0} max={6} value={form.decimals} onChange={set("decimals")} readOnly={!canEdit || !isNew} dir="ltr" />}
          </Field>
        </div>
        <div className="actions">
          <button type="button" onClick={onClose}>{t("inv.close_form")}</button>
          {canEdit && <button type="submit" className="primary" disabled={busy}>{busy ? t("loading") : t("inv.save")}</button>}
        </div>
      </form>
    </Card>
  );
}
