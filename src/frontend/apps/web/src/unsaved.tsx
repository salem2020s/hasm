import { useI18n } from "@hasm/ui";
import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { useAuth } from "./auth";
import { ConfirmDialog } from "./components";
import { isTauri } from "./platform";
import { setNavigationBlocker } from "./router";

/**
 * Unsaved-changes safeguard. Pages report which of their sections have unsaved edits (by key); anything that would leave the page or end
 * the editing context (navigation, Change company, Sign out) goes through `guard`, which asks Stay / Discard first. Nothing is ever saved
 * automatically and the dialog only names the sections (never field values). Reload / close uses the browser's native confirmation.
 */
interface UnsavedApi {
  /** Report (or clear) unsaved edits of a section. `label` is the already translated section name shown in the dialog. */
  setDirty(key: string, dirty: boolean, label: string): void;
  /** Runs `action` now when nothing is unsaved, otherwise after the user chose "Discard changes". */
  guard(action: () => void): void;
}

const UnsavedContext = createContext<UnsavedApi | null>(null);

/** Routes that keep the unsaved sections alive (moving between them is not "leaving"). */
const EDITING_ROUTES = "/company/details";

export function UnsavedProvider({ children }: { children: ReactNode }) {
  const { t } = useI18n();
  const dirty = useRef<Record<string, string>>({}); // key -> label, only while dirty
  const [pending, setPending] = useState<null | { run: () => void; labels: string[] }>(null);

  const auth = useAuth();
  // D-236: while the shell is locked (the session ended with unsaved input) nothing may leave the current page or ask a question on top of the sign-in-again dialog.
  const locked = useRef(false);
  locked.current = auth.locked;

  const isDirty = () => Object.keys(dirty.current).length > 0;
  const setDirty = useCallback((key: string, d: boolean, label: string) => { if (d) dirty.current[key] = label; else delete dirty.current[key]; }, []);
  const guard = useCallback((action: () => void) => {
    if (locked.current) return;                             // locked: the request is ignored (the address is put back by the router); the page and its input stay as they are
    if (!isDirty()) { action(); return; }
    // One dialog at a time: a second request while it is open (e.g. pressing X again) is ignored, so there are no stacked dialogs or loops.
    setPending((open) => open ?? { run: action, labels: Object.values(dirty.current) });
  }, []);

  useEffect(() => {
    setNavigationBlocker({ isBlocked: (to) => locked.current || (isDirty() && !to.startsWith(EDITING_ROUTES)), request: guard });
    return () => setNavigationBlocker(null);
  }, [guard]);

  // Reload / close tab or window: the browser's native confirmation (the text itself is chosen by the browser).
  useEffect(() => {
    const on = (e: BeforeUnloadEvent) => { if (isDirty()) { e.preventDefault(); e.returnValue = ""; } };
    window.addEventListener("beforeunload", on);
    return () => window.removeEventListener("beforeunload", on);
  }, []);

  // Windows desktop only: the native window-close request (X button, Alt+F4, taskbar close). The official Tauri 2 window API is loaded
  // lazily so the web build never runs it. With unsaved edits the close is prevented and the same Stay / Discard dialog is shown;
  // "Discard changes" then destroys the window (destroy() does not raise another close request, so there is no loop). Without unsaved
  // edits the handler does nothing and Tauri closes the window as usual.
  useEffect(() => {
    if (!isTauri()) return;
    let cancelled = false;
    let unlisten: (() => void) | undefined;
    void import("@tauri-apps/api/window").then(async ({ getCurrentWindow }) => {
      const win = getCurrentWindow();
      const off = await win.onCloseRequested((event) => {
        if (!isDirty()) return;
        event.preventDefault();
        guard(() => { void win.destroy(); });
      });
      if (cancelled) off(); else unlisten = off;
    }).catch(() => { /* if the window API is unavailable the app simply behaves as before */ });
    return () => { cancelled = true; unlisten?.(); };
  }, [guard]);

  // The session provider asks this registry whether anything is unsaved when the session ends, and empties it when the preserved input is dropped (D-236).
  const { attachUnsaved } = auth;
  useEffect(() => {
    attachUnsaved({ isDirty, clear: () => { dirty.current = {}; setPending(null); } });
    return () => attachUnsaved(null);
  }, [attachUnsaved]);
  // An open Stay / Discard question is withdrawn when the shell locks: the sign-in-again dialog is the only dialog then.
  useEffect(() => { if (auth.locked) setPending(null); }, [auth.locked]);

  const api = useMemo<UnsavedApi>(() => ({ setDirty, guard }), [setDirty, guard]);
  return (
    <UnsavedContext.Provider value={api}>
      {children}
      {pending && !auth.locked && (
        <ConfirmDialog
          title={t("unsaved.title")}
          message={t("unsaved.message", { sections: pending.labels.join(t("unsaved.joiner")) })}
          cancelLabel={t("unsaved.stay")}
          confirmLabel={t("unsaved.discard")}
          onCancel={() => setPending(null)}
          onConfirm={() => { const run = pending.run; dirty.current = {}; setPending(null); run(); }}
        />
      )}
    </UnsavedContext.Provider>
  );
}

export function useUnsaved(): UnsavedApi {
  const ctx = useContext(UnsavedContext);
  if (!ctx) throw new Error("useUnsaved must be used inside <UnsavedProvider>");
  return ctx;
}

/** Report a section's unsaved state for as long as the component is mounted (cleared on unmount). */
export function useReportUnsaved(key: string, dirty: boolean, label: string): void {
  const { setDirty } = useUnsaved();
  useEffect(() => { setDirty(key, dirty, label); return () => setDirty(key, false, label); }, [setDirty, key, dirty, label]);
}
