import { ApiError, type AuthStatus, type EnrolmentSecret } from "@hasm/api-client";
import { useI18n } from "@hasm/ui";
import { useCallback, useEffect, useRef, useState, type FormEvent } from "react";
import { useAuth } from "../auth";
import { Banner, Card, Field, Loading, PageHeader } from "../components";
import { EnrolmentSecretBlock } from "../mfaParts";
import { useReportUnsaved } from "../unsaved";

/**
 * The user's own security settings (Phase 4.1 S3b-2a): the state of their two-step verification and the VOLUNTARY enrolment of an authenticator. One implementation for the browser and the desktop app; the server decides
 * everything (who may enrol, the password check, the code, the throttle) and every refusal here is a message, never a decision.
 * Voluntary enrolment (only for a user without two-step verification): the password is confirmed, the server shows the provisioning secret ONCE (as text), and a code from the new authenticator confirms it; the server then
 * answers with a NEW session (the old one is revoked) which the app adopts. The password, the secret and the code live only in this component's state: they are cleared as soon as they are used, never stored and never logged.
 * There are no recovery codes in Release 1 (decision D-097); replacing or removing an authenticator is not offered here.
 */
type Mfa = NonNullable<AuthStatus["mfa"]>;

export function SecurityPage() {
  const { t, locale } = useI18n();
  const auth = useAuth();
  const [mfa, setMfa] = useState<Mfa | null | undefined>(undefined);       // undefined: loading; null: the server reports no state
  const [loadError, setLoadError] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);
  const seq = useRef(0);

  const load = useCallback(async () => {
    const n = ++seq.current;
    try {
      const status = await auth.api.status();
      if (n !== seq.current) return;
      if (!status.authenticated) { auth.handleError(new ApiError(401, "unauthenticated")); return; }
      setMfa(status.mfa ?? null);
      setLoadError(false);
    } catch (e) {
      if (n !== seq.current || auth.handleError(e)) return;
      setLoadError(true);
    }
  }, [auth]);
  useEffect(() => { void load(); }, [load]);

  const verifiedAt = mfa?.verifiedAtUtc ? new Date(mfa.verifiedAtUtc).toLocaleString(locale === "ar" ? "ar" : "en", { dateStyle: "medium", timeStyle: "short" }) : null;

  if (mfa === undefined && !loadError) return <Loading />;
  return (
    <div>
      <PageHeader title={t("sec.title")} description={t("sec.subtitle")} />
      {loadError && <Banner kind="error" testId="sec-load-error">{t("sec.load_error")} <button type="button" className="link" onClick={() => void load()}>{t("picker.retry")}</button></Banner>}
      {notice && <Banner kind="success" testId="sec-notice">{notice}</Banner>}
      {mfa !== undefined && (
        <Card title={t("sec.mfa.title")} hint={t("sec.mfa.hint")}>
          <dl className="kv" data-testid="sec-state">
            <dt>{t("sec.mfa.status")}</dt>
            <dd><span className={`chip ${mfa?.state === "enrolled" ? "chip-ok" : "chip-off"}`} data-testid="sec-state-chip">{t(`sec.mfa.state.${mfa?.state ?? "unknown"}`)}</span></dd>
            {mfa?.required !== undefined && (<><dt>{t("sec.mfa.required")}</dt><dd data-testid="sec-required">{mfa.required ? t("sec.mfa.required_yes") : t("sec.mfa.required_no")}</dd></>)}
            {verifiedAt && (<><dt>{t("sec.mfa.verified_at")}</dt><dd><bdi dir="ltr">{verifiedAt}</bdi></dd></>)}
          </dl>
          {mfa?.state === "none" && <VoluntaryEnrolment onDone={async () => { setNotice(t("sec.enrol.done")); await load(); }} />}
          {mfa?.state === "enrolled" && <p className="hint" data-testid="sec-enrolled-note">{t("sec.mfa.enrolled_note")}</p>}
          {(mfa?.state === "enrolmentrequired" || mfa?.state === "recoveryrequired") && <p className="hint" data-testid="sec-other-note">{t("sec.mfa.other_note")}</p>}
          {mfa === null && <p className="hint">{t("sec.mfa.unknown_note")}</p>}
        </Card>
      )}
    </div>
  );
}

function VoluntaryEnrolment({ onDone }: { onDone: () => Promise<void> }) {
  const { t } = useI18n();
  const auth = useAuth();
  const [password, setPassword] = useState("");
  const [code, setCode] = useState("");
  const [secret, setSecret] = useState<EnrolmentSecret | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [fieldError, setFieldError] = useState<string | undefined>();
  // The secret is shown once: leaving the page with it on screen would lose it, so it counts as unsaved.
  useReportUnsaved("security-enrolment", secret !== null, t("sec.enrol.section"));
  useEffect(() => () => { setSecret(null); setPassword(""); setCode(""); }, []);

  /** A 401 here is one refusal for every cause (wrong password or code, or no session): ask the server whether the session is still alive before blaming the input. */
  const refused = useCallback(async (e: unknown, wrong: string) => {
    if (e instanceof ApiError && e.status === 429) { setSecret(null); setCode(""); setError(t("mfa.throttled")); return; }   // throttled: the secret is not kept on the page; the user starts again later
    if (e instanceof ApiError && e.status === 401) {
      try { if (!(await auth.api.status()).authenticated) { auth.handleError(new ApiError(401, "unauthenticated")); return; } } catch { /* fall through to the message */ }
      setError(wrong);
      return;
    }
    if (e instanceof ApiError) setError(t("error.generic")); else setError(t("error.network"));
  }, [auth, t]);

  async function begin(e: FormEvent) {
    e.preventDefault();
    if (busy) return;
    if (!password) { setFieldError(t("field.required")); return; }
    setFieldError(undefined); setError(null); setBusy(true);
    const used = password;
    setPassword("");                                                                    // the password is dropped from memory as soon as it is sent
    try { setSecret(await auth.api.beginEnrolment({ password: used })); }
    catch (err) { await refused(err, t("sec.enrol.begin_refused")); }
    finally { setBusy(false); }
  }

  async function confirm(e: FormEvent) {
    e.preventDefault();
    if (busy) return;
    const digits = code.trim().replace(/[٠-٩]/g, (d) => String(d.charCodeAt(0) - 0x0660)).replace(/[۰-۹]/g, (d) => String(d.charCodeAt(0) - 0x06f0));
    if (!/^\d{6}$/.test(digits)) { setFieldError(t("mfa.step_up.format")); return; }
    setFieldError(undefined); setError(null); setBusy(true);
    setCode("");
    try {
      const result = await auth.api.confirmEnrolment({ code: digits });
      if (!("user" in result) || !result.user) { setError(t("sec.enrol.confirm_refused")); return; }
      setSecret(null);                                                                   // the secret is gone from the page; the enrolment is done
      await auth.adoptEnrolledSession(result.user);
      await onDone();
    } catch (err) { await refused(err, t("sec.enrol.confirm_refused")); }
    finally { setBusy(false); }
  }

  if (!secret) {
    return (
      <section aria-labelledby="sec-enrol-title" data-testid="sec-enrol-begin">
        <h2 id="sec-enrol-title">{t("sec.enrol.title")}</h2>
        <p className="hint">{t("sec.enrol.intro")}</p>
        {error && <Banner kind="error" testId="sec-error">{error}</Banner>}
        <form onSubmit={begin} noValidate>
          <Field id="sec-password" label={t("sec.enrol.password")} hint={t("sec.enrol.password_hint")} error={fieldError} required>
            {(aria) => <input {...aria} type="password" name="password" autoComplete="current-password" value={password} onChange={(e) => setPassword(e.target.value)} dir="ltr" />}
          </Field>
          <div className="actions"><button type="submit" className="primary" disabled={busy}>{busy ? t("loading") : t("sec.enrol.start")}</button></div>
        </form>
      </section>
    );
  }
  return (
    <section aria-labelledby="sec-enrol-title" data-testid="sec-enrol-confirm">
      <h2 id="sec-enrol-title">{t("sec.enrol.title")}</h2>
      <p className="hint">{t("mfa.enrol.secret_intro")}</p>
      <EnrolmentSecretBlock secret={secret} />
      <p className="hint">{t("mfa.enrol.confirm_intro")}</p>
      {error && <Banner kind="error" testId="sec-error">{error}</Banner>}
      <form onSubmit={confirm} noValidate>
        <Field id="sec-code" label={t("mfa.code")} error={fieldError} required>
          {(aria) => <input {...aria} name="code" inputMode="numeric" autoComplete="one-time-code" maxLength={6} value={code} onChange={(e) => setCode(e.target.value)} dir="ltr" />}
        </Field>
        <div className="actions">
          <button type="submit" className="primary" disabled={busy}>{busy ? t("loading") : t("mfa.enrol.confirm")}</button>
          <button type="button" disabled={busy} onClick={() => { setSecret(null); setCode(""); setError(null); setFieldError(undefined); }}>{t("sec.enrol.cancel")}</button>
        </div>
      </form>
    </section>
  );
}
