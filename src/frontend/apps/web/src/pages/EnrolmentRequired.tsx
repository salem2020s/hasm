import { ApiError, type AuthStatus } from "@hasm/api-client";
import { useI18n } from "@hasm/ui";
import { useCallback, useEffect, useRef, useState } from "react";
import { useAuth } from "../auth";
import { Banner, Loading } from "../components";
import { VoluntaryEnrolment } from "./Security";

/**
 * The selected company's MFA policy applies to the signed-in user, who has no authenticator (Phase 4.1 S3b-2b). The server answered 403 `mfa_enrolment_required`, either on the company selection or on a company route of a company
 * that is already selected; the app shows this one prompt for both and does NOT end the session. The user enrols here with the same shared enrolment as the security settings (password, secret shown once as text, code); the new
 * session the server issues is adopted and the company is selected again. Nothing is retried in a loop: the prompt calls no company route. The server decides everything; this screen only explains and offers the way out
 * (another company, or sign out).
 */
export function EnrolmentRequiredPage() {
  const { t } = useI18n();
  const auth = useAuth();
  const [state, setState] = useState<NonNullable<AuthStatus["mfa"]>["state"] | null | undefined>(undefined);   // undefined: loading; null: the server reports none
  const [loadError, setLoadError] = useState(false);
  const [busy, setBusy] = useState(false);
  const seq = useRef(0);

  const load = useCallback(async () => {
    const n = ++seq.current;
    try {
      const status = await auth.api.status();
      if (n !== seq.current) return;
      if (!status.authenticated) { auth.handleError(new ApiError(401, "unauthenticated")); return; }
      setState(status.mfa?.state ?? null);
      setLoadError(false);
    } catch (e) {
      if (n !== seq.current || auth.handleError(e)) return;
      setLoadError(true);
    }
  }, [auth.api, auth.handleError]);   // stable: the provider's state changes while the enrolment completes (new user, re-selection) must NOT ask again, or the answer "enrolled" would flash the administrator warning
  useEffect(() => { void load(); }, [load]);

  async function another() {
    if (busy) return;
    setBusy(true);
    try { await auth.changeCompany(); } finally { setBusy(false); }
  }

  return (
    <section aria-labelledby="page-title" data-testid="enrolment-required">
      <h1 id="page-title" className="auth-title">{t("enrol_required.title")}</h1>
      <p className="page-description auth-sub">{t("enrol_required.intro")}</p>
      {loadError && <Banner kind="error" testId="enrol-required-load-error">{t("sec.load_error")} <button type="button" className="link" onClick={() => void load()}>{t("picker.retry")}</button></Banner>}
      {state === undefined && !loadError && <Loading />}
      {state === "none" && <VoluntaryEnrolment onDone={async () => { /* the new session is adopted and the company selected again: this prompt is replaced */ }} />}
      {(state === "enrolmentrequired" || state === "recoveryrequired") && <Banner kind="warning" testId="enrol-required-other">{t("sec.mfa.other_note")}</Banner>}
      {(state === "enrolled" || state === null) && <Banner kind="warning" testId="enrol-required-unknown">{t("enrol_required.other")}</Banner>}
      <div className="actions">
        <button type="button" className="ghost" disabled={busy} onClick={() => void another()} data-testid="enrol-required-another">{t("enrol_required.another")}</button>
        <button type="button" className="ghost" onClick={() => void auth.logout()} data-testid="enrol-required-logout">{t("nav.logout")}</button>
      </div>
    </section>
  );
}
