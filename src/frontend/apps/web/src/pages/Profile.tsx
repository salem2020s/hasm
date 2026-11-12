import { ApiError, type ProfileFields, type ProfileResponse } from "@hasm/api-client";
import { useI18n } from "@hasm/ui";
import { useCallback, useEffect, useRef, useState, type FormEvent } from "react";
import { useAuth } from "../auth";
import { Banner, Card, errorText, Field, fieldErrors, Loading, PageHeader } from "../components";
import { COUNTRY_CODES, countryName } from "../countries";

type Form = Record<keyof ProfileFields, string>;
const KEYS: (keyof ProfileFields)[] = ["nameAr", "nameEn", "legalName", "commercialRegistrationNo", "taxRegistrationNo", "countryCode",
  "addressLine1", "addressLine2", "city", "region", "postalCode", "phone", "email", "website"];

const empty = (): Form => Object.fromEntries(KEYS.map((k) => [k, ""])) as Form;
const toForm = (p: ProfileFields | null): Form => (p ? (Object.fromEntries(KEYS.map((k) => [k, p[k] ?? ""])) as Form) : empty());
const blankToNull = (s: string) => (s.trim() ? s.trim() : null);

/** `embedded`: rendered as a section of the unified Company Details & Settings page (no page header; reports unsaved edits to the parent). */
export function ProfilePage({ embedded = false, onDirty }: { embedded?: boolean; onDirty?: (dirty: boolean) => void } = {}) {
  const { t, locale } = useI18n();
  // The loader must not depend on the translation function: a language switch must never reload the data and overwrite unsaved edits.
  const tRef = useRef(t);
  tRef.current = t;
  const auth = useAuth();
  const canEdit = auth.can("company.profile.update");
  const [loaded, setLoaded] = useState<ProfileResponse | null>(null);
  const [form, setForm] = useState<Form>(empty());
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [loadError, setLoadError] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [conflict, setConflict] = useState(false);
  const [saved, setSaved] = useState<ProfileResponse | null>(null);
  const [busy, setBusy] = useState(false);

  const load = useCallback(async () => {
    try {
      const r = await auth.api.getProfile();
      setLoaded(r);
      setForm(toForm(r.profile));
      setLoadError(null);
    } catch (e) {
      if (!auth.handleError(e)) setLoadError(errorText(tRef.current, e));
    }
  }, [auth.api, auth.handleError]);
  useEffect(() => { void load(); }, [load]);

  const dirty = !!loaded && KEYS.some((k) => form[k] !== toForm(loaded.profile)[k]);
  useEffect(() => { onDirty?.(dirty); }, [dirty, onDirty]);

  if (loadError) return <Banner kind="error">{loadError}</Banner>;
  if (!loaded) return <Loading />;

  const set = (k: keyof ProfileFields) => (e: { target: { value: string } }) => setForm((f) => ({ ...f, [k]: e.target.value }));

  function validate(): Record<string, string> {
    const v: Record<string, string> = {};
    if (!form.nameAr.trim() && !form.nameEn.trim()) { v.nameAr = t("field.name_required"); v.nameEn = t("field.name_required"); }
    if (!form.countryCode) v.countryCode = t("field.required");
    if (form.email.trim() && !/^[^@\s]+@[^@\s]+\.[^@\s]+$/.test(form.email.trim())) v.email = t("field.invalid_format");
    if (form.website.trim() && !/^https?:\/\/\S+$/i.test(form.website.trim())) v.website = t("field.invalid_url");
    return v;
  }

  async function submit(e: FormEvent) {
    e.preventDefault();
    if (!canEdit || !loaded) return;
    const v = validate();
    setErrors(v);
    setSaved(null);
    if (Object.keys(v).length > 0) { setError(t("form.fix_errors")); return; }
    setBusy(true);
    setError(null);
    setConflict(false);
    try {
      const body = Object.fromEntries(KEYS.map((k) => [k, blankToNull(form[k])])) as unknown as ProfileFields;
      const r = await auth.api.putProfile(body, loaded.revision);
      setLoaded(r);
      setForm(toForm(r.profile));
      setSaved(r);
      void auth.refreshCompanies();
    } catch (err) {
      // A failed update changes nothing: the form keeps what the user typed and the stored data is untouched.
      if (auth.handleError(err)) return;
      if (err instanceof ApiError && err.status === 412) setConflict(true);
      else if (err instanceof ApiError && err.status === 400) { setErrors(fieldErrors(t, err)); setError(t("form.fix_errors")); }
      else setError(errorText(t, err));
    } finally {
      setBusy(false);
    }
  }

  const text = (k: keyof ProfileFields, label: string, extra: { required?: boolean; dir?: "ltr" | "rtl"; type?: string; autoComplete?: string } = {}) => (
    <Field id={`p-${k}`} label={label} error={errors[k]} required={extra.required}>
      {(aria) => <input {...aria} name={k} value={form[k]} onChange={set(k)} readOnly={!canEdit} dir={extra.dir} type={extra.type ?? "text"} autoComplete={extra.autoComplete ?? "off"} />}
    </Field>
  );

  const Root = embedded ? "div" : "section";
  return (
    <Root {...(embedded ? {} : { "aria-labelledby": "page-title" })}>
      {!embedded && <PageHeader title={t("profile.title")} description={t("profile.subtitle")} />}
      {!loaded.profile && <Banner kind="info" testId="profile-new">{t("profile.not_set")}</Banner>}
      {!canEdit && <Banner kind="info" testId="read-only">{t("form.read_only")}</Banner>}
      {conflict && (
        <Banner kind="error" testId="conflict">
          {t("form.conflict")}{" "}
          <button type="button" onClick={() => { setConflict(false); void load(); }}>{t("form.reload_latest")}</button>
        </Banner>
      )}
      {error && <Banner kind="error">{error}</Banner>}
      {saved && <Banner kind="success" testId="saved">{t("form.saved")}</Banner>}
      {saved?.projection === "pending" && (
        <Banner kind="warning" testId="projection-pending">
          {t("profile.projection_pending")}{" "}
          {canEdit && <button type="button" onClick={async () => {
            try { const r = await auth.api.syncDisplayName(); setSaved(r); void auth.refreshCompanies(); } catch (e) { if (!auth.handleError(e)) setError(errorText(t, e)); }
          }}>{t("profile.retry_sync")}</button>}
        </Banner>
      )}
      <form onSubmit={submit} noValidate className="form-grid">
        <Card title={t("profile.names")} hint={t("profile.names_hint")}>
         <div className="fields">
          {text("nameAr", t("profile.name_ar"), { dir: "rtl" })}
          {text("nameEn", t("profile.name_en"), { dir: "ltr" })}
          {text("legalName", t("profile.legal_name"))}
         </div>
        </Card>
        <Card title={t("profile.identifiers")}>
         <div className="fields">
          {text("commercialRegistrationNo", t("profile.registration_no"), { dir: "ltr" })}
          {text("taxRegistrationNo", t("profile.tax_no"), { dir: "ltr" })}
         </div>
        </Card>
        <Card title={t("profile.location")}>
         <div className="fields">
          <Field id="p-countryCode" label={t("profile.country")} error={errors.countryCode} required>
            {(aria) => (
              <select {...aria} name="countryCode" value={form.countryCode} onChange={set("countryCode")} disabled={!canEdit}>
                <option value="">{t("form.select")}</option>
                {COUNTRY_CODES.map((c) => <option key={c} value={c}>{countryName(c, locale)} ({c})</option>)}
              </select>
            )}
          </Field>
          {text("addressLine1", t("profile.address1"), { autoComplete: "address-line1" })}
          {text("addressLine2", t("profile.address2"), { autoComplete: "address-line2" })}
          {text("city", t("profile.city"))}
          {text("region", t("profile.region"))}
          {text("postalCode", t("profile.postal_code"), { dir: "ltr" })}
         </div>
        </Card>
        <Card title={t("profile.contact")}>
         <div className="fields">
          {text("phone", t("profile.phone"), { dir: "ltr", type: "tel" })}
          {text("email", t("profile.email"), { dir: "ltr", type: "email" })}
          {text("website", t("profile.website"), { dir: "ltr", type: "url" })}
         </div>
        </Card>
        {canEdit && <div className="actions"><button type="submit" className="primary" disabled={busy}>{busy ? t("loading") : t("form.save")}</button></div>}
      </form>
    </Root>
  );
}
