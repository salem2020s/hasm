import { ApiError } from "@hasm/api-client";
import { useI18n } from "@hasm/ui";
import { useState, type FormEvent } from "react";
import { useAuth } from "../auth";
import { Banner, Field } from "../components";
import { EnrolmentSecretBlock } from "../mfaParts";

/**
 * The steps between a correct password and a session (Phase 4.1): the second factor, the enrolment of a required authenticator, and the recovery notice.
 * One implementation for the browser and the desktop app: the server decides every step and every refusal looks the same; this screen only shows it. Nothing here is stored.
 */
export function MfaStep() {
  const auth = useAuth();
  const mfa = auth.mfa;
  if (!mfa) return null;
  if (mfa.kind === "mfa_recovery_required") return <RecoveryNotice />;
  if (mfa.kind === "mfa_enrolment_required") return <EnrolmentSteps />;
  return <CodeStep />;
}

function useSubmit(action: (value: string) => Promise<void>, invalidKey: string) {
  const { t } = useI18n();
  const [value, setValue] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [fieldError, setFieldError] = useState<string | undefined>();

  async function submit(e: FormEvent) {
    e.preventDefault();
    const text = value.trim();
    setFieldError(text ? undefined : t("field.required"));
    if (!text) return;
    setBusy(true);
    setError(null);
    try {
      await action(text);
      setValue("");
    } catch (err) {
      setValue("");
      if (err instanceof ApiError && err.status === 429) setError(t("mfa.throttled"));
      else if (err instanceof ApiError && err.status === 401) setError(t(invalidKey));
      else if (err instanceof ApiError) setError(t("error.generic"));
      else setError(t("error.network"));
    } finally {
      setBusy(false);
    }
  }
  return { value, setValue, busy, error, fieldError, submit };
}

function CodeStep() {
  const { t } = useI18n();
  const auth = useAuth();
  const f = useSubmit(auth.submitMfaCode, "mfa.code_invalid");
  return (
    <section aria-labelledby="page-title">
      <h1 id="page-title" className="auth-title">{t("mfa.title")}</h1>
      <p className="page-description auth-sub">{t("mfa.intro")}</p>
      {f.error && <Banner kind="error">{f.error}</Banner>}
      <form onSubmit={f.submit} noValidate>
        <Field id="mfa-code" label={t("mfa.code")} error={f.fieldError} required>
          {(aria) => <input {...aria} name="code" inputMode="numeric" autoComplete="one-time-code" maxLength={6} autoFocus value={f.value} onChange={(e) => f.setValue(e.target.value)} dir="ltr" />}
        </Field>
        <button type="submit" className="primary block" disabled={f.busy}>{f.busy ? t("loading") : t("mfa.verify")}</button>
        <button type="button" className="link block" onClick={auth.cancelMfa}>{t("mfa.cancel")}</button>
      </form>
    </section>
  );
}

function EnrolmentSteps() {
  const { t } = useI18n();
  const auth = useAuth();
  const secret = auth.mfa?.secret ?? null;
  const begin = useSubmit(auth.beginEnrolment, "mfa.enrol.invalid");
  const confirm = useSubmit(auth.confirmEnrolment, "mfa.code_invalid");
  return (
    <section aria-labelledby="page-title">
      <h1 id="page-title" className="auth-title">{t("mfa.enrol.title")}</h1>
      {!secret ? (
        <>
          <p className="page-description auth-sub">{t("mfa.enrol.intro")}</p>
          {begin.error && <Banner kind="error">{begin.error}</Banner>}
          <form onSubmit={begin.submit} noValidate>
            <Field id="mfa-enrol-code" label={t("mfa.enrol.code")} error={begin.fieldError} required>
              {(aria) => <input {...aria} name="enrolmentCode" autoComplete="off" autoCapitalize="characters" autoFocus value={begin.value} onChange={(e) => begin.setValue(e.target.value)} dir="ltr" />}
            </Field>
            <button type="submit" className="primary block" disabled={begin.busy}>{begin.busy ? t("loading") : t("mfa.enrol.start")}</button>
            <button type="button" className="link block" onClick={auth.cancelMfa}>{t("mfa.cancel")}</button>
          </form>
        </>
      ) : (
        <>
          <p className="page-description auth-sub">{t("mfa.enrol.secret_intro")}</p>
          <EnrolmentSecretBlock secret={secret} />
          <p className="page-description">{t("mfa.enrol.confirm_intro")}</p>
          {confirm.error && <Banner kind="error">{confirm.error}</Banner>}
          <form onSubmit={confirm.submit} noValidate>
            <Field id="mfa-confirm-code" label={t("mfa.code")} error={confirm.fieldError} required>
              {(aria) => <input {...aria} name="code" inputMode="numeric" autoComplete="one-time-code" maxLength={6} autoFocus value={confirm.value} onChange={(e) => confirm.setValue(e.target.value)} dir="ltr" />}
            </Field>
            <button type="submit" className="primary block" disabled={confirm.busy}>{confirm.busy ? t("loading") : t("mfa.enrol.confirm")}</button>
          </form>
        </>
      )}
    </section>
  );
}

function RecoveryNotice() {
  const { t } = useI18n();
  const auth = useAuth();
  return (
    <section aria-labelledby="page-title">
      <h1 id="page-title" className="auth-title">{t("mfa.recovery.title")}</h1>
      <Banner kind="warning" testId="mfa-recovery">{t("mfa.recovery.body")}</Banner>
      <button type="button" className="link block" onClick={auth.cancelMfa}>{t("mfa.cancel")}</button>
    </section>
  );
}
