import { ApiError } from "@hasm/api-client";
import { useI18n } from "@hasm/ui";
import { useCallback, useEffect, useRef, useState } from "react";
import { useAuth } from "./auth";
import { Banner, ConfirmDialog, Field } from "./components";

/**
 * MFA step-up (Phase 4.1): when the server refuses a privileged action only because the second-factor proof is not fresh, the shared api client asks this bridge for a fresh code. One implementation for the browser and
 * the desktop app. The bridge lets the client (created before React) reach the dialog without a circular import, like the session bridge. Several refusals at once share ONE dialog.
 */
export function createStepUpBridge() {
  const bridge = { listener: null as null | (() => Promise<boolean>), request: (): Promise<boolean> => (bridge.listener ? bridge.listener() : Promise.resolve(false)) };
  return bridge;
}
export type StepUpBridge = ReturnType<typeof createStepUpBridge>;

const toAscii = (s: string) => s.trim().replace(/[\u0660-\u0669]/g, (d) => String(d.charCodeAt(0) - 0x0660)).replace(/[\u06f0-\u06f9]/g, (d) => String(d.charCodeAt(0) - 0x06f0));

/** Renders the step-up dialog on demand. Mount once, inside the authentication provider. */
export function StepUpHost({ bridge }: { bridge: StepUpBridge }) {
  const { t } = useI18n();
  const auth = useAuth();
  const [open, setOpen] = useState(false);
  const pending = useRef<{ promise: Promise<boolean>; settle: (ok: boolean) => void } | null>(null);
  const [code, setCode] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [fieldError, setFieldError] = useState<string | undefined>();
  const input = useRef<HTMLInputElement>(null);

  useEffect(() => {
    bridge.listener = () => {
      if (pending.current) return pending.current.promise;                         // a second refusal while the dialog is open joins the same dialog
      let settle: (ok: boolean) => void = () => undefined;
      const promise = new Promise<boolean>((resolve) => { settle = resolve; });
      pending.current = { promise, settle };
      setCode(""); setError(null); setFieldError(undefined); setBusy(false); setOpen(true);
      return promise;
    };
    return () => { bridge.listener = null; pending.current?.settle(false); pending.current = null; };
  }, [bridge]);

  useEffect(() => { if (open) { const id = requestAnimationFrame(() => input.current?.focus()); return () => cancelAnimationFrame(id); } return undefined; }, [open]);
  // D-236: the session ended while this dialog was open. It is withdrawn (the waiting request is reported as cancelled) and the typed code is forgotten.
  useEffect(() => {
    if (!auth.locked) return;
    const p = pending.current; pending.current = null;
    p?.settle(false);
    setOpen(false); setCode(""); setError(null); setFieldError(undefined);
  }, [auth.locked]);

  const finish = useCallback((ok: boolean) => {
    const p = pending.current;
    pending.current = null;
    setOpen(false); setCode(""); setBusy(false);                                   // the code is dropped from memory as soon as the dialog closes
    p?.settle(ok);
  }, []);

  const submit = useCallback(async () => {
    if (busy) return;
    const digits = toAscii(code);
    if (!/^\d{6}$/.test(digits)) { setFieldError(t("mfa.step_up.format")); return; }
    setFieldError(undefined); setError(null); setBusy(true);
    try {
      await auth.api.mfaStepUp(digits);
      finish(true);
    } catch (e) {
      setCode(""); setBusy(false);
      if (e instanceof ApiError && e.status === 401 && e.code === "invalid_code") setError(t("mfa.step_up.wrong"));       // wrong or replayed: stay open, the session is untouched
      else if (e instanceof ApiError && e.status === 429) setError(t("mfa.throttled"));
      else if (auth.handleError(e)) finish(false);                                                                       // the session itself is gone: sign out
      else if (e instanceof ApiError) setError(t("error.generic"));
      else setError(t("error.network"));
      requestAnimationFrame(() => input.current?.focus());
    }
  }, [auth, busy, code, finish, t]);

  if (!open) return null;
  return (
    <ConfirmDialog title={t("mfa.step_up.title")} message={t("mfa.step_up.message")} confirmLabel={busy ? t("loading") : t("mfa.verify")} onConfirm={() => void submit()} onCancel={() => finish(false)} confirmDisabled={busy}>
      {error && <Banner kind="error" testId="step-up-error">{error}</Banner>}
      <Field id="step-up-code" label={t("mfa.code")} error={fieldError} required>
        {(aria) => <input {...aria} ref={input} name="code" inputMode="numeric" autoComplete="one-time-code" maxLength={6} value={code} dir="ltr" onChange={(e) => setCode(e.target.value)}
          onKeyDown={(e) => { if (e.key === "Enter") { e.preventDefault(); void submit(); } }} />}
      </Field>
    </ConfirmDialog>
  );
}
