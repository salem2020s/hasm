import { ApiError } from "@hasm/api-client";
import { useI18n } from "@hasm/ui";
import { useState, type FormEvent } from "react";
import { useAuth } from "../auth";
import { Banner, Field } from "../components";

export function LoginPage() {
  const { t } = useI18n();
  const auth = useAuth();
  const [login, setLogin] = useState("");
  const [password, setPassword] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [fieldError, setFieldError] = useState<{ login?: string; password?: string }>({});

  async function submit(e: FormEvent) {
    e.preventDefault();
    const missing = { login: login.trim() ? undefined : t("field.required"), password: password ? undefined : t("field.required") };
    setFieldError(missing);
    if (missing.login || missing.password) return;
    setBusy(true);
    setError(null);
    try {
      await auth.login(login.trim(), password);
      setPassword(""); // never kept in memory longer than needed
    } catch (err) {
      setPassword("");
      if (err instanceof ApiError && err.status === 401) setError(t("error.invalid_credentials"));
      else if (err instanceof ApiError && err.status === 429)
        setError(err.details.retryAfterSeconds ? t("error.too_many_attempts_wait", { seconds: err.details.retryAfterSeconds }) : t("error.too_many_attempts"));
      else if (err instanceof ApiError) setError(t("error.generic"));
      else setError(t("error.network"));
    } finally {
      setBusy(false);
    }
  }

  return (
    <section aria-labelledby="page-title">
      <h1 id="page-title" className="auth-title">{t("login.title")}</h1>
      <p className="page-description auth-sub">{t("tagline")}</p>
      {auth.notice === "session_expired" && <Banner kind="warning" testId="notice-expired">{t("login.session_expired")}</Banner>}
      {auth.notice === "logged_out" && <Banner kind="success" testId="notice-logged-out">{t("login.logged_out")}</Banner>}
      {error && <Banner kind="error">{error}</Banner>}
      <form onSubmit={submit} noValidate>
        <Field id="login" label={t("login.user")} error={fieldError.login} required>
          {(aria) => <input {...aria} name="login" autoComplete="username" value={login} onChange={(e) => setLogin(e.target.value)} dir="ltr" />}
        </Field>
        <Field id="password" label={t("login.password")} error={fieldError.password} required>
          {(aria) => <input {...aria} name="password" type="password" autoComplete="current-password" value={password} onChange={(e) => setPassword(e.target.value)} dir="ltr" />}
        </Field>
        <button type="submit" className="primary block" disabled={busy}>{busy ? t("loading") : t("login.submit")}</button>
      </form>
    </section>
  );
}
