import { useI18n } from "@hasm/ui";
import { useEffect, useRef, useState, type ReactNode } from "react";
import { useAuth } from "./auth";
import { AuthIdPrefix, Banner } from "./components";
import { LoginPage } from "./pages/Login";

/**
 * Sign in again after the session ended with unsaved input (owner decision D-236). One implementation for the browser and the desktop app.
 *
 * What is behind this dialog: the application shell, still mounted, with `hidden`, `inert` and `aria-hidden` (see Gate in App.tsx). Its React state IS the preserved
 * input; it is never copied, serialized or stored. This dialog shows nothing of it: no user name, no company, no section names. It is the ordinary sign-in form
 * (password, then the second factor, an enrolment or the recovery notice, exactly as at any sign-in) plus one way out, "Discard and sign out".
 * The password and code fields belong to the sign-in form, are never part of anything preserved, and disappear with this dialog on every outcome.
 */
export const REAUTH_IDS = "reauth-";

export function ReauthDialog({ frame }: { frame: (children: ReactNode) => ReactNode }) {
  const { t } = useI18n();
  const auth = useAuth();
  const root = useRef<HTMLDivElement>(null);
  const [busy, setBusy] = useState(false);

  // Focus moves into the dialog (the shell is inert, so focus could otherwise be left on a hidden control).
  useEffect(() => {
    const first = root.current?.querySelector<HTMLElement>("input, button");
    first?.focus();
  }, []);

  return (
    <div ref={root} className="reauth" role="dialog" aria-modal="true" aria-labelledby={`${REAUTH_IDS}page-title`} aria-describedby="reauth-message" data-testid="reauth-dialog">
      {frame(
        <>
          <Banner kind="warning" testId="reauth-message"><span id="reauth-message">{t("reauth.message")}</span></Banner>
          {/* Ids of its own: the hidden shell page has a `page-title` and fields too, and a shared id would name this dialog after the HIDDEN screen. */}
          <AuthIdPrefix.Provider value={REAUTH_IDS}><LoginPage /></AuthIdPrefix.Provider>
          <div className="reauth-discard">
            <button type="button" className="ghost" data-testid="reauth-discard" disabled={busy} onClick={() => { setBusy(true); void auth.discardLocked(); }}>{t("reauth.discard")}</button>
          </div>
        </>,
      )}
    </div>
  );
}
