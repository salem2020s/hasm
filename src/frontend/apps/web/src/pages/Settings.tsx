import { ApiError, type SettingsFields, type SettingsResponse } from "@hasm/api-client";
import { useI18n } from "@hasm/ui";
import { useCallback, useEffect, useRef, useState, type FormEvent } from "react";
import { useAuth } from "../auth";
import { Banner, Card, ConfirmDialog, errorText, Field, fieldErrors, Loading, PageHeader } from "../components";
import { timeZones } from "../countries";

/** `embedded`: rendered as a section of the unified Company Details & Settings page (no page header; reports unsaved edits to the parent). */
export function SettingsPage({ embedded = false, onDirty }: { embedded?: boolean; onDirty?: (dirty: boolean) => void } = {}) {
  const { t } = useI18n();
  // The loader must not depend on the translation function: a language switch must never reload the data and overwrite unsaved edits.
  const tRef = useRef(t);
  tRef.current = t;
  const auth = useAuth();
  const canEdit = auth.can("company.settings.update");
  const [loaded, setLoaded] = useState<SettingsResponse | null>(null);
  const [form, setForm] = useState<SettingsFields | null>(null);
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [loadError, setLoadError] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [conflict, setConflict] = useState(false);
  const [saved, setSaved] = useState(false);
  const [busy, setBusy] = useState(false);
  const [confirm, setConfirm] = useState(false);

  const load = useCallback(async () => {
    try {
      const r = await auth.api.getSettings();
      setLoaded(r);
      setForm(r.settings);
      setLoadError(null);
    } catch (e) {
      if (!auth.handleError(e)) setLoadError(errorText(tRef.current, e));
    }
  }, [auth.api, auth.handleError]);
  useEffect(() => { void load(); }, [load]);

  const dirty = !!loaded && !!form && JSON.stringify(form) !== JSON.stringify(loaded.settings);
  useEffect(() => { onDirty?.(dirty); }, [dirty, onDirty]);

  if (loadError) return <Banner kind="error">{loadError}</Banner>;
  if (!loaded || !form) return <Loading />;

  const zoneChanged = loaded.settings.timeZone !== null && form.timeZone !== loaded.settings.timeZone;

  async function save() {
    if (!loaded || !form) return;
    setBusy(true);
    setError(null);
    setConflict(false);
    setSaved(false);
    try {
      const r = await auth.api.putSettings(form, loaded.revision);
      setLoaded(r);
      setForm(r.settings);
      setErrors({});
      setSaved(true);
    } catch (err) {
      if (auth.handleError(err)) return;
      if (err instanceof ApiError && err.status === 412) setConflict(true);
      else if (err instanceof ApiError && err.status === 400) { setErrors(fieldErrors(t, err)); setError(t("form.fix_errors")); }
      else setError(errorText(t, err));
    } finally {
      setBusy(false);
    }
  }

  function submit(e: FormEvent) {
    e.preventDefault();
    if (!canEdit) return;
    // Changing an established business time zone is a sensitive action: confirm first.
    if (zoneChanged) setConfirm(true);
    else void save();
  }

  const zones = timeZones();
  const optionLabel = (group: string, v: string) => t(`settings.${group}.${v}`);

  const Root = embedded ? "div" : "section";
  return (
    <Root {...(embedded ? {} : { "aria-labelledby": "page-title" })}>
      {!embedded && <PageHeader title={t("settings.title")} description={t("settings.subtitle")} />}
      {!loaded.setupComplete && <Banner kind="warning" testId="setup-incomplete">{t("settings.setup_incomplete")}</Banner>}
      {!canEdit && <Banner kind="info" testId="read-only">{t("form.read_only")}</Banner>}
      {conflict && (
        <Banner kind="error" testId="conflict">
          {t("form.conflict")}{" "}
          <button type="button" onClick={() => { setConflict(false); void load(); }}>{t("form.reload_latest")}</button>
        </Banner>
      )}
      {error && <Banner kind="error">{error}</Banner>}
      {saved && <Banner kind="success" testId="saved">{t("form.saved")}</Banner>}
      <form onSubmit={submit} noValidate className="form-grid">
       <Card title={t("settings.group_regional")}>
        <div className="fields">
        <Field id="s-lang" label={t("settings.default_language")} hint={t("settings.default_language_hint")} error={errors.defaultLanguage}>
          {(aria) => (
            <select {...aria} value={form.defaultLanguage} disabled={!canEdit} onChange={(e) => setForm({ ...form, defaultLanguage: e.target.value })}>
              {loaded.options.languages.map((v) => <option key={v} value={v}>{optionLabel("language", v)}</option>)}
            </select>
          )}
        </Field>
        <Field id="s-date" label={t("settings.date_format")} error={errors.dateFormat}>
          {(aria) => (
            <select {...aria} value={form.dateFormat} disabled={!canEdit} onChange={(e) => setForm({ ...form, dateFormat: e.target.value })}>
              {loaded.options.dateFormats.map((v) => <option key={v} value={v}>{v}</option>)}
            </select>
          )}
        </Field>
        <Field id="s-num" label={t("settings.numeral_system")} error={errors.numeralSystem}>
          {(aria) => (
            <select {...aria} value={form.numeralSystem} disabled={!canEdit} onChange={(e) => setForm({ ...form, numeralSystem: e.target.value })}>
              {loaded.options.numeralSystems.map((v) => <option key={v} value={v}>{optionLabel("numerals", v)}</option>)}
            </select>
          )}
        </Field>
        <Field id="s-tz" label={t("settings.time_zone")} hint={t("settings.time_zone_hint")} error={errors.timeZone}>
          {(aria) => (
            <select {...aria} value={form.timeZone ?? ""} disabled={!canEdit} onChange={(e) => setForm({ ...form, timeZone: e.target.value || null })}>
              <option value="">{t("settings.time_zone_unset")}</option>
              {zones.map((z) => <option key={z} value={z}>{z}</option>)}
            </select>
          )}
        </Field>
        </div>
       </Card>
        {canEdit && <div className="actions"><button type="submit" className="primary" disabled={busy}>{busy ? t("loading") : t("form.save")}</button></div>}
      </form>
      {confirm && (
        <ConfirmDialog
          title={t("settings.confirm_zone_title")}
          message={t("settings.confirm_zone_message")}
          confirmLabel={t("form.confirm")}
          onCancel={() => setConfirm(false)}
          onConfirm={() => { setConfirm(false); void save(); }}
        />
      )}
    </Root>
  );
}
