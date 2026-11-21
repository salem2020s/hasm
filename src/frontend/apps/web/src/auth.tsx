import { ApiError, type ApiClient, type CompanySummary, type EnrolmentSecret, type LoginStep, type SessionUser } from "@hasm/api-client";
import { useI18n } from "@hasm/ui";
import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from "react";

/**
 * Session state lives ONLY in memory. The session itself is the HttpOnly Secure cookie (web) or an in-memory bearer token (desktop); the
 * server is the source of truth and is asked on every start (`/auth/status`). Nothing sensitive (token, password, permissions) is ever
 * written to localStorage/sessionStorage; the only stored value is the non-sensitive UI language.
 */
export type Phase = "loading" | "unreachable" | "anonymous" | "mfa" | "needCompany" | "needEnrolment" | "ready";
/** The server answers 403 with this code when the selected company's MFA policy applies to the user and the user has no authenticator (Phase 4.1 S3b-1b). It is not a permission refusal and never ends the session. */
export const ENROLMENT_REQUIRED = "mfa_enrolment_required";
export const isEnrolmentRequired = (e: unknown): boolean => e instanceof ApiError && e.status === 403 && e.code === ENROLMENT_REQUIRED;
/** The unfinished second-factor step of a login (Phase 4.1). Held in memory only; the challenge is single use, bound to this client and expires in five minutes. */
export interface MfaFlow { kind: LoginStep["kind"]; challenge: string | null; secret: EnrolmentSecret | null }
export type Notice = "session_expired" | "logged_out" | null;

export interface AuthValue {
  phase: Phase;
  user: SessionUser | null;
  companies: CompanySummary[];
  companyId: string | null;
  notice: Notice;
  api: ApiClient;
  login(login: string, password: string): Promise<void>;
  /** Set while a login waits for its second factor, an enrolment or a recovery (phase "mfa"). */
  mfa: MfaFlow | null;
  /** Second step of a login: the 6-digit code. Throws ApiError (401 is the one refusal; 429 is throttled). */
  submitMfaCode(code: string): Promise<void>;
  /** Starts an enrolment with the one-time enrolment code; the secret is shown once from the returned value. */
  beginEnrolment(enrolmentCode: string): Promise<void>;
  /** Confirms the enrolment with a code from the new authenticator; ends in a signed-in session. */
  confirmEnrolment(code: string): Promise<void>;
  /** Abandons the second step and returns to the password form. */
  cancelMfa(): void;
  /**
   * After a VOLUNTARY enrolment (security settings) the server answers with a NEW session and revokes the old one: keeps the user, selects the same company again on the new session and reloads the permissions.
   * A failure to re-enter falls back to company selection (never a half-signed-in screen).
   */
  adoptEnrolledSession(user: SessionUser): Promise<void>;
  logout(): Promise<void>;
  chooseCompany(id: string): Promise<void>;
  changeCompany(): Promise<void>;
  refreshCompanies(): Promise<void>;
  can(permission: string): boolean;
  /** True when ANY grant (company-wide or for a single branch) holds the permission. Only for showing a screen; the server decides per branch. */
  canAny(permission: string): boolean;
  /**
   * The permission for ONE branch, like the server's decision for a branch-scoped resource: a company-wide grant covers every branch, a grant for a single branch covers only that branch. A grant for ANOTHER branch
   * does not count. For hiding or disabling a control on a branch-scoped document; the server still decides.
   */
  canOnBranch(permission: string, branchId: string): boolean;
  /** True when the user holds this ROLE in ANY grant, like the server (which reads the role ids from every grant and scopes only the approve PERMISSION to the document's branch). Read from the access answer; only for showing or hiding a button: a stage bound to a role is enforced by the server. */
  hasRole(roleId: string): boolean;
  companyName: string | null;
  /** Page-level API failures: returns true (and ends the session in the UI) when the server answered 401. */
  handleError(error: unknown): boolean;
}

const AuthContext = createContext<AuthValue | null>(null);

/** Lets the api client tell the provider that the server ended the session (401) without a circular import. */
export function createSessionBridge() {
  const bridge = {
    listener: null as null | (() => void), fire: () => bridge.listener?.(),
    /** 403 `mfa_enrolment_required` on any authenticated call; `scope` is what {@link scope} answered when that call STARTED. */
    enrolmentListener: null as null | ((scope?: unknown) => void), fireEnrolment: (scope?: unknown) => bridge.enrolmentListener?.(scope),
    /** The current company-selection attempt (a counter the provider owns). The api client reads it when a call starts, so an answer that arrives after the selection changed can be told apart. */
    scopeListener: null as null | (() => unknown), scope: (): unknown => bridge.scopeListener?.(),
  };
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
  const [anyPermissions, setAnyPermissions] = useState<ReadonlySet<string>>(new Set());
  const [branchPermissions, setBranchPermissions] = useState<ReadonlyMap<string, ReadonlySet<string>>>(new Map());
  const [roleGrants, setRoleGrants] = useState<readonly { roleId: string; scope: string; scopeId: string | null }[]>([]);
  const [notice, setNotice] = useState<Notice>(null);
  const [mfa, setMfa] = useState<MfaFlow | null>(null);
  const mounted = useRef(true);
  /** The company whose selection the server answered 403 mfa_enrolment_required: selected again after the enrolment. */
  const pendingCompany = useRef<string | null>(null);
  /** The selected company as the callbacks below see it (the state value lags a render behind). */
  const companyRef = useRef<string | null>(null);
  /** Counts the company-selection attempts and clears: an enrolment-required answer of a call started under an older value belongs to a previous company and is ignored. */
  const epoch = useRef(0);
  /** A company selection (or the session restoration) is being answered by the server right now. */
  const selecting = useRef(0);
  const setCompany = useCallback((id: string | null) => { companyRef.current = id; setCompanyId(id); }, []);

  const endSession = useCallback((why: Notice) => {
    setUser(null);
    setCompanies([]);
    setCompany(null);
    setPermissions(new Set());
    setAnyPermissions(new Set());
    setRoleGrants([]);
    setBranchPermissions(new Map());
    pendingCompany.current = null;
    epoch.current++;
    setNotice(why);
    setPhase("anonymous");
  }, [setCompany]);

  /** The company's MFA policy applies and the user has no authenticator: the enrolment prompt replaces the company screens. Idempotent (many calls may fail at once); the session stays and nothing is retried here. */
  const requireEnrolment = useCallback(() => setPhase((p) => (p === "anonymous" || p === "mfa" ? p : "needEnrolment")), []);

  /**
   * The same, for a signal that comes from OUTSIDE the provider's own selection flows (the api client's hook, or a page handing its error to `handleError`). It is honoured only when it belongs to the current selection attempt or
   * company: a call that started before the selection changed (`scope` is older than the current attempt) is ignored, and so is any signal while no company is selected and no selection is being answered. A late refusal of a
   * previous company's request therefore cannot raise the prompt from company selection or inside another company.
   */
  const signalEnrolment = useCallback((scope?: unknown) => {
    if (scope !== undefined && scope !== epoch.current) return;
    if (companyRef.current === null && selecting.current === 0) return;
    requireEnrolment();
  }, [requireEnrolment]);

  const handleError = useCallback((error: unknown) => {
    if (isEnrolmentRequired(error)) { signalEnrolment((error as ApiError).details.scope); return true; }   // handled either way: a stale refusal is not shown as a page error
    if (error instanceof ApiError && error.status === 401 && error.code !== "mfa_step_up_required" && error.code !== "mfa_step_up_cancelled") {   // a stale second-factor proof is not a session end
      endSession("session_expired");
      return true;
    }
    return false;
  }, [endSession, signalEnrolment]);

  useEffect(() => {
    if (bridge) { bridge.listener = () => endSession("session_expired"); bridge.enrolmentListener = signalEnrolment; bridge.scopeListener = () => epoch.current; }
    return () => { if (bridge) { bridge.listener = null; bridge.enrolmentListener = null; bridge.scopeListener = null; } };
  }, [bridge, endSession, signalEnrolment]);

  /** Runs one company selection (or the restoration) as the current attempt: calls started before it no longer count, and its own refusal does. */
  const attempt = useCallback(async <T,>(run: () => Promise<T>): Promise<T> => {
    epoch.current++;
    selecting.current++;
    try { return await run(); } finally { selecting.current--; }
  }, []);

  const enterCompany = useCallback(async () => {
    const me = await api.accessMe();
    setCompany(me.companyId);
    setPermissions(new Set(me.grants.filter((g) => g.scope === "company").flatMap((g) => g.permissions)));
    setAnyPermissions(new Set(me.grants.flatMap((g) => g.permissions)));
    const perBranch = new Map<string, Set<string>>();
    for (const g of me.grants) if (g.scope === "branch" && g.scopeId) { const set = perBranch.get(g.scopeId) ?? new Set<string>(); g.permissions.forEach((p) => set.add(p)); perBranch.set(g.scopeId, set); }
    setBranchPermissions(perBranch);
    setRoleGrants(me.grants.map((g) => ({ roleId: g.roleId, scope: g.scope, scopeId: g.scopeId })));
    setPhase("ready");
  }, [api, setCompany]);

  const loadCompanies = useCallback(async () => {
    setCompanies((await api.listCompanies()).companies);
  }, [api]);

  /**
   * Exactly one accessible company: enter it through the normal select-company API (the server re-checks membership and company
   * state; nothing is manufactured in the frontend). Any failure simply leaves the user on the Company Selection screen, where
   * choosing the company retries the same call. More than one (or none) always shows the selection screen.
   * Returns true when the company was entered.
   */
  const autoEnterSole = useCallback(async (list: CompanySummary[]): Promise<boolean> => {
    if (list.length !== 1) return false;
    try {
      await attempt(async () => { await api.selectCompany(list[0].id); await enterCompany(); });
      return true;
    } catch (e) {
      if (e instanceof ApiError && e.status === 401) throw e;
      if (isEnrolmentRequired(e)) { pendingCompany.current = list[0].id; requireEnrolment(); return true; }   // the company's policy needs an authenticator first: the prompt is shown, the screen is not "needCompany"
      return false;
    }
  }, [api, attempt, enterCompany, requireEnrolment]);

  // Session restoration: the server decides (cookie), the client keeps no session data of its own.
  useEffect(() => {
    mounted.current = true;
    (async () => {
      try {
        const status = await api.status();
        if (!mounted.current) return;
        if (!status.authenticated || !status.user) { setPhase("anonymous"); return; }
        setUser(status.user);
        const list = (await api.listCompanies()).companies;
        setCompanies(list);
        try { await attempt(enterCompany); } catch (e) {
          if (e instanceof ApiError && e.status === 401) throw e;
          if (isEnrolmentRequired(e)) { requireEnrolment(); return; }     // a company is selected on the server and its policy needs an authenticator (which company is not known here: see adoptEnrolledSession)
          // 409 company_not_selected (or no usable selection): enter the only company, otherwise ask.
          if (!(await autoEnterSole(list))) setPhase("needCompany");
        }
      } catch (e) {
        if (!mounted.current) return;
        if (e instanceof ApiError && e.status === 401) setPhase("anonymous");
        else setPhase("unreachable");
      }
    })();
    return () => { mounted.current = false; };
  }, [api, attempt, enterCompany, autoEnterSole, requireEnrolment]);

  const finishLogin = useCallback(async (signedIn: SessionUser) => {
    setMfa(null);
    setUser(signedIn);
    setNotice(null);
    const list = (await api.listCompanies()).companies;
    setCompanies(list);
    if (!(await autoEnterSole(list))) setPhase("needCompany");
  }, [api, autoEnterSole]);

  const login = useCallback(async (loginName: string, password: string) => {
    const result = await api.login(loginName, password); // throws ApiError: the form shows the mapped message
    if (result.step) {                                   // a second factor, an enrolment or a recovery stands between the password and a session
      setMfa({ kind: result.step.kind, challenge: result.step.kind === "mfa_recovery_required" ? null : result.step.challenge, secret: null });
      setPhase("mfa");
      return;
    }
    await finishLogin(result.user);
  }, [api, finishLogin]);

  const submitMfaCode = useCallback(async (code: string) => {
    if (!mfa?.challenge) throw new ApiError(401, "invalid_credentials");
    const result = await api.verifyMfa(mfa.challenge, code);
    if (result.step) throw new ApiError(401, "invalid_credentials");
    await finishLogin(result.user);
  }, [api, mfa, finishLogin]);

  const beginEnrolment = useCallback(async (enrolmentCode: string) => {
    if (!mfa?.challenge) throw new ApiError(401, "invalid_credentials");
    const secret = await api.beginEnrolment({ challenge: mfa.challenge, enrolmentCode });
    setMfa({ ...mfa, secret: { otpauthUri: secret.otpauthUri, secret: secret.secret } });
  }, [api, mfa]);

  const confirmEnrolment = useCallback(async (code: string) => {
    if (!mfa?.challenge) throw new ApiError(401, "invalid_credentials");
    const result = await api.confirmEnrolment({ challenge: mfa.challenge, code });
    if (!("user" in result) || !result.user) throw new ApiError(401, "invalid_credentials");
    await finishLogin(result.user);
  }, [api, mfa, finishLogin]);

  const cancelMfa = useCallback(() => { setMfa(null); setPhase("anonymous"); }, []);

  const adoptEnrolledSession = useCallback(async (enrolled: SessionUser) => {
    setUser(enrolled);
    let target = pendingCompany.current ?? companyRef.current;
    pendingCompany.current = null;
    try {
      if (!target) {
        // The prompt came from a session restored with a company already selected on the server: the server refused before saying which one, so it is not known here. With exactly one accessible company it is that one (entered
        // like after a login); with several the user chooses again.
        const list = (await api.listCompanies()).companies;
        setCompanies(list);
        if (list.length === 1) target = list[0].id;
      }
      if (target) { const id = target; await attempt(async () => { await api.selectCompany(id); await enterCompany(); }); return; }
    } catch (e) { if (e instanceof ApiError && e.status === 401) { handleError(e); return; } }
    epoch.current++;
    setCompany(null); setPermissions(new Set()); setAnyPermissions(new Set()); setRoleGrants([]); setBranchPermissions(new Map());   // nothing of the previous company survives into company selection (as changeCompany)
    await loadCompanies();
    setPhase("needCompany");
  }, [api, attempt, enterCompany, handleError, loadCompanies, setCompany]);

  const logout = useCallback(async () => {
    try { await api.logout(); } catch { /* the server session may already be gone; the local state is cleared regardless */ }
    endSession("logged_out");
  }, [api, endSession]);

  const chooseCompany = useCallback(async (id: string) => {
    try {
      await attempt(async () => { await api.selectCompany(id); await enterCompany(); });
    } catch (e) {
      if (!isEnrolmentRequired(e)) throw e;
      pendingCompany.current = id;        // the server undid the selection; it is made again after the enrolment
      requireEnrolment();
    }
  }, [api, attempt, enterCompany, requireEnrolment]);

  const changeCompany = useCallback(async () => {
    epoch.current++;                       // whatever is still in flight belongs to the company being left
    try { await api.clearCompany(); } catch (e) { if (handleError(e)) return; }
    setCompany(null);
    setPermissions(new Set());
    setAnyPermissions(new Set());
    setRoleGrants([]);
    setBranchPermissions(new Map());
    try { await loadCompanies(); } catch (e) { if (handleError(e)) return; }
    setPhase("needCompany");
  }, [api, handleError, loadCompanies, setCompany]);

  const refreshCompanies = useCallback(async () => {
    try { await loadCompanies(); } catch (e) { handleError(e); }
  }, [handleError, loadCompanies]);

  const value = useMemo<AuthValue>(() => ({
    phase, user, companies, companyId, notice, api, login, mfa, submitMfaCode, beginEnrolment, confirmEnrolment, cancelMfa, adoptEnrolledSession, logout, chooseCompany, changeCompany, refreshCompanies, handleError,
    can: (p) => permissions.has(p),
    canAny: (p) => anyPermissions.has(p),
    canOnBranch: (p, branchId) => permissions.has(p) || (branchPermissions.get(branchId)?.has(p) ?? false),
    hasRole: (r) => roleGrants.some((g) => g.roleId === r),
    companyName: displayCompanyName(companies.find((c) => c.id === companyId), locale),
  }), [phase, user, companies, companyId, notice, api, login, mfa, submitMfaCode, beginEnrolment, confirmEnrolment, cancelMfa, adoptEnrolledSession, logout, chooseCompany, changeCompany, refreshCompanies, handleError, permissions, anyPermissions, branchPermissions, roleGrants, locale]);

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthValue {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error("useAuth must be used inside <AuthProvider>");
  return ctx;
}
