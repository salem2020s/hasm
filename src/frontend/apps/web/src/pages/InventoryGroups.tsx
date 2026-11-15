import { ApiError, type InventoryCapabilities, type InventoryGroup, type InventorySettings, type ValuationMethodCode } from "@hasm/api-client";
import { useI18n } from "@hasm/ui";
import { useCallback, useEffect, useRef, useState, type FormEvent } from "react";
import { useAuth } from "../auth";
import { Banner, Card, ConfirmDialog, errorText, Field, Loading, PageHeader } from "../components";
import { useReportUnsaved, useUnsaved } from "../unsaved";
import { blankToNull, descendantIds, groupTree, invFieldErrors, invName } from "./inventoryCommon";

/**
 * Item groups and the valuation policy (Phase 3.4). ONE implementation for Web and Windows Desktop. A group's valuation method is its own setting, else its parent's, else the company default
 * (FIFO or moving weighted average, D-017); only the POLICY is recorded here: the costing engine is a later phase. All rules are enforced by the server and PostgreSQL.
 */
const CODE_SHAPE = /^[A-Z0-9][A-Z0-9_-]{0,19}$/;
type Editor = { mode: "new" } | { mode: "edit"; group: InventoryGroup };

export function InventoryGroupsPage() {
  const { t, locale } = useI18n();
  const tRef = useRef(t);
  tRef.current = t;
  const auth = useAuth();
  const { guard } = useUnsaved();
  const [groups, setGroups] = useState<InventoryGroup[] | null>(null);
  const [caps, setCaps] = useState<InventoryCapabilities | null>(null);
  const [settings, setSettings] = useState<InventorySettings | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [editor, setEditor] = useState<Editor | null>(null);
  const [confirm, setConfirm] = useState<InventoryGroup | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [conflict, setConflict] = useState(false);
  const [busy, setBusy] = useState(false);

  const load = useCallback(async () => {
    try {
      const [g, c] = await Promise.all([auth.api.listInventoryGroups(), auth.api.getInventoryCapabilities()]);
      setGroups(g.groups);
      setCaps(c);
      setSettings(c.settingsView ? await auth.api.getInventorySettings() : null);
      setLoadError(null);
    } catch (e) {
      if (!auth.handleError(e)) setLoadError(errorText(tRef.current, e, "inv.error."));
    }
  }, [auth.api, auth.handleError]);
  useEffect(() => { void load(); }, [load]);

  if (loadError) return <Banner kind="error">{loadError}</Banner>;
  if (!groups || !caps) return <Loading />;
  const method = (m: ValuationMethodCode | null) => (m === 1 ? t("inv.valuation.fifo") : m === 2 ? t("inv.valuation.average") : t("inv.valuation.undecided"));
  const tree = groupTree(groups);

  async function toggle(g: InventoryGroup) {
    setConfirm(null);
    setBusy(true); setError(null); setConflict(false); setNotice(null);
    try {
      await (g.isActive ? auth.api.deactivateInventoryGroup(g.id, g.revision) : auth.api.activateInventoryGroup(g.id, g.revision));
      await load();
      setNotice(t("inv.changed"));
    } catch (err) {
      if (auth.handleError(err)) return;
      if (err instanceof ApiError && err.status === 412) setConflict(true); else setError(errorText(t, err, "inv.error."));
    } finally { setBusy(false); }
  }

  return (
    <section aria-labelledby="page-title">
      <PageHeader title={t("inv.groups.title")} description={t("inv.groups.subtitle")}
        actions={caps.groupsCreate && <button type="button" className="primary" disabled={busy} onClick={() => guard(() => { setNotice(null); setEditor({ mode: "new" }); })}>{t("inv.groups.new")}</button>} />
      {!caps.groupsCreate && !caps.groupsUpdate && <Banner kind="info" testId="read-only">{t("form.read_only")}</Banner>}
      {conflict && <Banner kind="error" testId="conflict">{t("inv.error.revision_conflict")} <button type="button" onClick={() => { setConflict(false); void load(); }}>{t("form.reload_latest")}</button></Banner>}
      {error && <Banner kind="error">{error}</Banner>}
      {notice && <Banner kind="success" testId="saved">{notice}</Banner>}
      {settings && <DefaultValuation settings={settings} canEdit={caps.settingsConfigure} onSaved={(m) => { setNotice(m); void load(); }} />}
      <Card title={t("inv.groups.list")}>
        <div className="table-wrap">
          <table className="data-table">
            <caption className="visually-hidden">{t("inv.groups.list")}</caption>
            <thead><tr><th scope="col">{t("inv.code")}</th><th scope="col">{t("inv.name")}</th><th scope="col">{t("inv.groups.valuation")}</th><th scope="col">{t("inv.status")}</th><th scope="col">{t("inv.actions")}</th></tr></thead>
            <tbody>
              {tree.length === 0 && <tr><td colSpan={5}>{t("inv.groups.none")}</td></tr>}
              {tree.map(({ group: g, depth }) => (
                <tr key={g.id} data-testid={`group-${g.code}`}>
                  <td><bdi dir="ltr" style={{ paddingInlineStart: `${depth * 1.25}rem` }}>{g.code}</bdi></td><td><bdi>{invName(g, locale)}</bdi></td>
                  <td>{g.valuation === null ? <>{t("inv.groups.inherits")}: {method(g.effectiveValuation)}</> : method(g.valuation)}</td>
                  <td><span className={`chip ${g.isActive ? "chip-ok" : "chip-off"}`}>{g.isActive ? t("inv.active") : t("inv.inactive")}</span></td>
                  <td>
                    <button type="button" disabled={busy} onClick={() => guard(() => { setNotice(null); setEditor({ mode: "edit", group: g }); })}>{caps.groupsUpdate ? t("inv.edit") : t("inv.view")}</button>{" "}
                    {caps.groupsActivate && <button type="button" disabled={busy} onClick={() => setConfirm(g)}>{g.isActive ? t("inv.deactivate") : t("inv.activate")}</button>}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </Card>
      {editor && <GroupForm key={editor.mode === "edit" ? editor.group.id : "new"} editor={editor} groups={groups} canEdit={editor.mode === "new" ? caps.groupsCreate : caps.groupsUpdate}
        onClose={() => guard(() => setEditor(null))} onSaved={(m) => { setEditor(null); setNotice(m); void load(); }} />}
      {confirm && <ConfirmDialog title={confirm.isActive ? t("inv.deactivate_title") : t("inv.activate_title")} message={t(confirm.isActive ? "inv.groups.deactivate_message" : "inv.groups.activate_message", { code: confirm.code })}
        confirmLabel={t("form.confirm")} onCancel={() => setConfirm(null)} onConfirm={() => void toggle(confirm)} />}
    </section>
  );
}

function DefaultValuation({ settings, canEdit, onSaved }: { settings: InventorySettings; canEdit: boolean; onSaved: (m: string) => void }) {
  const { t } = useI18n();
  const auth = useAuth();
  const [value, setValue] = useState(settings.defaultValuation === null ? "" : String(settings.defaultValuation));
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const base = settings.defaultValuation === null ? "" : String(settings.defaultValuation);
  useReportUnsaved("inventory-settings", value !== base, t("inv.settings.section"));
  useEffect(() => setValue(base), [base]);
  async function save(e: FormEvent) {
    e.preventDefault();
    setBusy(true); setError(null);
    try { await auth.api.putInventorySettings(value === "" ? null : (Number(value) as ValuationMethodCode), settings.revision); onSaved(t("inv.settings.saved")); }
    catch (err) { if (!auth.handleError(err)) setError(errorText(t, err, "inv.error.")); }
    finally { setBusy(false); }
  }
  return (
    <Card title={t("inv.settings.title")} hint={t("inv.settings.hint")}>
      {error && <Banner kind="error" testId="settings-error">{error}</Banner>}
      <form onSubmit={save} noValidate className="form-grid">
        <Field id="s-valuation" label={t("inv.settings.default_valuation")} hint={settings.defaultValuation === null ? t("inv.settings.undecided_hint") : undefined}>
          {(aria) => (
            <select {...aria} value={value} onChange={(e) => setValue(e.target.value)} disabled={!canEdit}>
              <option value="">{t("inv.valuation.undecided")}</option>
              <option value="1">{t("inv.valuation.fifo")}</option>
              <option value="2">{t("inv.valuation.average")}</option>
            </select>
          )}
        </Field>
        {canEdit && <div className="actions"><button type="submit" className="primary" disabled={busy || value === base}>{busy ? t("loading") : t("inv.save")}</button></div>}
      </form>
    </Card>
  );
}

function GroupForm({ editor, groups, canEdit, onClose, onSaved }: { editor: Editor; groups: InventoryGroup[]; canEdit: boolean; onClose: () => void; onSaved: (m: string) => void }) {
  const { t, locale } = useI18n();
  const auth = useAuth();
  const isNew = editor.mode === "new";
  const initial = editor.mode === "edit" ? editor.group : null;
  const [revision, setRevision] = useState(initial?.revision ?? 0);
  const toForm = (g: InventoryGroup | null) => ({ code: g?.code ?? "", nameAr: g?.nameAr ?? "", nameEn: g?.nameEn ?? "", parentId: g?.parentId ?? "", valuation: g?.valuation == null ? "" : String(g.valuation) });
  const [base, setBase] = useState(toForm(initial));
  const [form, setForm] = useState(base);
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [error, setError] = useState<string | null>(null);
  const [conflict, setConflict] = useState(false);
  const [busy, setBusy] = useState(false);
  const dirty = (Object.keys(form) as (keyof typeof form)[]).some((k) => form[k] !== base[k]);
  useReportUnsaved("group-form", dirty, t("inv.groups.form_section"));
  const set = (k: keyof typeof form) => (e: { target: { value: string } }) => setForm((f) => ({ ...f, [k]: e.target.value }));
  const blocked = initial ? descendantIds(groups, initial.id) : new Set<string>();
  const parents = groupTree(groups.filter((g) => !blocked.has(g.id) && (g.isActive || g.id === base.parentId)));

  async function submit(e: FormEvent) {
    e.preventDefault();
    if (!canEdit) return;
    const v: Record<string, string> = {};
    if (isNew) { const code = form.code.trim().toUpperCase(); if (!code) v.code = t("field.required"); else if (!CODE_SHAPE.test(code)) v.code = t("field.invalid_format"); }
    if (!form.nameAr.trim() && !form.nameEn.trim()) { v.nameAr = t("inv.name_required"); v.nameEn = t("inv.name_required"); }
    setErrors(v);
    if (Object.keys(v).length > 0) { setError(t("form.fix_errors")); return; }
    setBusy(true); setError(null); setConflict(false);
    try {
      const body = { nameAr: blankToNull(form.nameAr), nameEn: blankToNull(form.nameEn), parentId: form.parentId || null, valuation: form.valuation === "" ? null : (Number(form.valuation) as ValuationMethodCode) };
      if (editor.mode === "new") { await auth.api.createInventoryGroup({ ...body, code: form.code.trim().toUpperCase() }); onSaved(t("inv.created")); }
      else { await auth.api.updateInventoryGroup(editor.group.id, body, revision); onSaved(t("inv.updated")); }
    } catch (err) {
      if (auth.handleError(err)) return;
      if (err instanceof ApiError && err.status === 412) setConflict(true);
      else if (err instanceof ApiError && err.status === 400) { setErrors(invFieldErrors(t, err)); setError(t("form.fix_errors")); }
      else if (err instanceof ApiError && err.code === "group_code_taken") { setErrors({ code: t("inv.error.group_code_taken") }); setError(t("form.fix_errors")); }
      else setError(errorText(t, err, "inv.error."));
    } finally { setBusy(false); }
  }

  async function reloadLatest() {
    if (editor.mode !== "edit") return;
    try {
      const latest = (await auth.api.listInventoryGroups()).groups.find((g) => g.id === editor.group.id);
      if (!latest) return;
      const next = toForm(latest);
      setRevision(latest.revision); setBase(next); setForm(next); setErrors({}); setConflict(false);
    } catch (err) { if (!auth.handleError(err)) setError(errorText(t, err, "inv.error.")); }
  }

  return (
    <Card title={isNew ? t("inv.groups.form_new") : t("inv.groups.form_edit", { code: base.code })}>
      {!canEdit && <Banner kind="info" testId="form-read-only">{t("form.read_only")}</Banner>}
      {conflict && <Banner kind="error" testId="form-conflict">{t("inv.error.revision_conflict")} <button type="button" onClick={() => void reloadLatest()}>{t("form.reload_latest")}</button></Banner>}
      {error && <Banner kind="error" testId="form-error">{error}</Banner>}
      <form onSubmit={submit} noValidate className="form-grid">
        <div className="fields">
          <Field id="g-code" label={t("inv.code")} error={errors.code} required={isNew} hint={isNew ? t("inv.code_hint") : undefined}>
            {(aria) => <input {...aria} name="code" value={form.code} onChange={set("code")} readOnly={!canEdit || !isNew} dir="ltr" autoComplete="off" />}
          </Field>
          <Field id="g-nameAr" label={t("inv.name_ar")} error={errors.nameAr}>{(aria) => <input {...aria} name="nameAr" value={form.nameAr} onChange={set("nameAr")} readOnly={!canEdit} dir="rtl" autoComplete="off" />}</Field>
          <Field id="g-nameEn" label={t("inv.name_en")} error={errors.nameEn}>{(aria) => <input {...aria} name="nameEn" value={form.nameEn} onChange={set("nameEn")} readOnly={!canEdit} dir="ltr" autoComplete="off" />}</Field>
          <Field id="g-parent" label={t("inv.groups.parent")} error={errors.parentId}>
            {(aria) => (
              <select {...aria} name="parentId" value={form.parentId} onChange={set("parentId")} disabled={!canEdit}>
                <option value="">{t("inv.groups.no_parent")}</option>
                {parents.map(({ group: g, depth }) => <option key={g.id} value={g.id}>{"  ".repeat(depth)}{g.code} — {invName(g, locale)}</option>)}
              </select>
            )}
          </Field>
          <Field id="g-valuation" label={t("inv.groups.valuation")} error={errors.valuation} hint={t("inv.groups.valuation_hint")}>
            {(aria) => (
              <select {...aria} name="valuation" value={form.valuation} onChange={set("valuation")} disabled={!canEdit}>
                <option value="">{t("inv.groups.inherit")}</option>
                <option value="1">{t("inv.valuation.fifo")}</option>
                <option value="2">{t("inv.valuation.average")}</option>
              </select>
            )}
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
