import { ApiError, type ApiClient, type CompanySummary, type SessionUser } from "@hasm/api-client";
import { useI18n } from "@hasm/ui";
import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from "react";

/**
 * Session state lives ONLY in memory. The session itself is the HttpOnly Secure cookie (web) or an in-memory bearer token (desktop); the
 * server is the source of truth and is asked on every start (`/auth/status`). Nothing sensitive (token, password, permissions) is ever
 * written to localStorage/sessionStorage; the only stored value is the non-sensitive UI language.
 */
export type Phase = "loading" | "unreachable" | "anonymous" | "needCompany" | "ready";
export type Notice = "session_expired" | "logged_out" | null;

export interface AuthValue {
  phase: Phase;
  user: SessionUser | null;
  companies: CompanySummary[];
  companyId: string | null;
  notice: Notice;
  api: ApiClient;
  login(login: string, password: string): Promise<void>;
  logout(): Promise<void>;
  chooseCompany(id: string): Promise<void>;
  changeCompany(): Promise<void>;
  refreshCompanies(): Promise<void>;
  can(permission: string): boolean;
  companyName: string | null;
  /** Page-level API failures: returns true (and ends the session in the UI) when the server answered 401. */
  handleError(error: unknown): boolean;
}

const AuthContext = createContext<AuthValue | null>(null);

/** Lets the api client tell the provider that the server ended the session (401) without a circular import. */
export function createSessionBridge() {
  const bridge = { listener: null as null | (() => void), fire: () => bridge.listener?.() };
  return bridge;
}
export type SessionBridge = ReturnType<typeof createSessionBridge>;

export function displayCompanyName(c: { nameAr: string | null; nameEn: string | null } | undefined, locale: string): string | null {
  if (!c) return null;
  const preferred = locale === "en" ? c.nameEn : c.nameAr;
  const other = locale === "en" ? c.nameAr : c.nameEn;
  return preferred?.trim() ? preferred : other?.trim() ? other : null;
}

export function AuthProvider({ api, bridge, children }: { api: ApiClient; bridge?: SessionBridge; children: ReactNode }) {
  const { locale } = useI18n();
  const [phase, setPhase] = useState<Phase>("loading");
  const [user, setUser] = useState<SessionUser | null>(null);
  const [companies, setCompanies] = useState<CompanySummary[]>([]);
  const [companyId, setCompanyId] = useState<string | null>(null);
  const [permissions, setPermissions] = useState<ReadonlySet<string>>(new Set());
  const [notice, setNotice] = useState<Notice>(null);
  const mounted = useRef(true);

  const endSession = useCallback((why: Notice) => {
    setUser(null);
    setCompanies([]);
    setCompanyId(null);
    setPermissions(new Set());
    setNotice(why);
    setPhase("anonymous");
  }, []);

  const handleError = useCallback((error: unknown) => {
    if (error instanceof ApiError && error.status === 401) {
      endSession("session_expired");
      return true;
    }
    return false;
  }, [endSession]);

  useEffect(() => {
    if (bridge) bridge.listener = () => endSession("session_expired");
    return () => { if (bridge) bridge.listener = null; };
  }, [bridge, endSession]);

  const enterCompany = useCallback(async () => {
    const me = await api.accessMe();
    setCompanyId(me.companyId);
    setPermissions(new Set(me.grants.filter((g) => g.scope === "company").flatMap((g) => g.permissions)));
    setPhase("ready");
  }, [api]);

  const loadCompanies = useCallback(async () => {
    setCompanies((await api.listCompanies()).companies);
  }, [api]);

  // Session restoration: the server decides (cookie), the client keeps no session data of its own.
  useEffect(() => {
    mounted.current = true;
    (async () => {
      try {
        const status = await api.status();
        if (!mounted.current) return;
        if (!status.authenticated || !status.user) { setPhase("anonymous"); return; }
        setUser(status.user);
        await loadCompanies();
        try { await enterCompany(); } catch (e) {
          if (e instanceof ApiError && e.status === 401) throw e;
          setPhase("needCompany"); // 409 company_not_selected (or no usable selection)
        }
      } catch (e) {
        if (!mounted.current) return;
        if (e instanceof ApiError && e.status === 401) setPhase("anonymous");
        else setPhase("unreachable");
      }
    })();
    return () => { mounted.current = false; };
  }, [api, enterCompany, loadCompanies]);

  const login = useCallback(async (loginName: string, password: string) => {
    const result = await api.login(loginName, password); // throws ApiError: the form shows the mapped message
    setUser(result.user);
    setNotice(null);
    await loadCompanies();
    setPhase("needCompany");
  }, [api, loadCompanies]);

  const logout = useCallback(async () => {
    try { await api.logout(); } catch { /* the server session may already be gone; the local state is cleared regardless */ }
    endSession("logged_out");
  }, [api, endSession]);

  const chooseCompany = useCallback(async (id: string) => {
    await api.selectCompany(id);
    await enterCompany();
  }, [api, enterCompany]);

  const changeCompany = useCallback(async () => {
    try { await api.clearCompany(); } catch (e) { if (handleError(e)) return; }
    setCompanyId(null);
    setPermissions(new Set());
    try { await loadCompanies(); } catch (e) { if (handleError(e)) return; }
    setPhase("needCompany");
  }, [api, handleError, loadCompanies]);

  const refreshCompanies = useCallback(async () => {
    try { await loadCompanies(); } catch (e) { handleError(e); }
  }, [handleError, loadCompanies]);

  const value = useMemo<AuthValue>(() => ({
    phase, user, companies, companyId, notice, api, login, logout, chooseCompany, changeCompany, refreshCompanies, handleError,
    can: (p) => permissions.has(p),
    companyName: displayCompanyName(companies.find((c) => c.id === companyId), locale),
  }), [phase, user, companies, companyId, notice, api, login, logout, chooseCompany, changeCompany, refreshCompanies, handleError, permissions, locale]);

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthValue {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error("useAuth must be used inside <AuthProvider>");
  return ctx;
}
