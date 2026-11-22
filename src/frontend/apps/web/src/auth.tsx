import { ApiError, type ApiClient, type CompanySummary, type EnrolmentSecret, type LoginStep, type SessionUser } from "@hasm/api-client";
import { useI18n } from "@hasm/ui";
import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from "react";

/**
 * Session state lives ONLY in memory. The session itself is the HttpOnly Secure cookie (web) or an in-memory bearer token (desktop); the
 * server is the source of truth and is asked on every start (`/auth/status`). Nothing sensitive (token, password, permissions) is ever
 * written to localStorage/sessionStorage; the only stored value is the non-sensitive UI language.
 */
export type Phase = "loading" | "unreachable" | "anonymous" | "mfa" | "needCompany" | "ready";
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
  /**
   * True when the user holds this ROLE for ONE branch, like the server's stage authority: through a company-wide assignment or an assignment for exactly this branch. The role held for another branch, a warehouse or
   * a cost center does not count. Only for showing or hiding a control on a role-bound approval stage; the server decides.
   */
  hasRoleOnBranch(roleId: string, branchId: string): boolean;
  /** True when the user holds this ROLE through any COST-CENTER assignment. The server decides whether those cost centers cover a journal entry (it knows the tree and the allocations); the screen only uses this to avoid hiding a control the server may allow. */
  hasRoleOnCostCenter(roleId: string): boolean;
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
  const [anyPermissions, setAnyPermissions] = useState<ReadonlySet<string>>(new Set());
  const [branchPermissions, setBranchPermissions] = useState<ReadonlyMap<string, ReadonlySet<string>>>(new Map());
  const [roleGrants, setRoleGrants] = useState<readonly { roleId: string; scope: string; scopeId: string | null }[]>([]);
  const [notice, setNotice] = useState<Notice>(null);
  const [mfa, setMfa] = useState<MfaFlow | null>(null);
  const mounted = useRef(true);

  const endSession = useCallback((why: Notice) => {
    setUser(null);
    setCompanies([]);
    setCompanyId(null);
    setPermissions(new Set());
    setAnyPermissions(new Set());
    setRoleGrants([]);
    setBranchPermissions(new Map());
    setNotice(why);
    setPhase("anonymous");
  }, []);

  const handleError = useCallback((error: unknown) => {
    if (error instanceof ApiError && error.status === 401 && error.code !== "mfa_step_up_required" && error.code !== "mfa_step_up_cancelled") {   // a stale second-factor proof is not a session end
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
    setAnyPermissions(new Set(me.grants.flatMap((g) => g.permissions)));
    const perBranch = new Map<string, Set<string>>();
    for (const g of me.grants) if (g.scope === "branch" && g.scopeId) { const set = perBranch.get(g.scopeId) ?? new Set<string>(); g.permissions.forEach((p) => set.add(p)); perBranch.set(g.scopeId, set); }
    setBranchPermissions(perBranch);
    setRoleGrants(me.grants.map((g) => ({ roleId: g.roleId, scope: g.scope, scopeId: g.scopeId })));
    setPhase("ready");
  }, [api]);

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
      await api.selectCompany(list[0].id);
      await enterCompany();
      return true;
    } catch (e) {
      if (e instanceof ApiError && e.status === 401) throw e;
      return false;
    }
  }, [api, enterCompany]);

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
        try { await enterCompany(); } catch (e) {
          if (e instanceof ApiError && e.status === 401) throw e;
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
  }, [api, enterCompany, autoEnterSole]);

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
    const previous = companyId;
    try {
      if (previous) { await api.selectCompany(previous); await enterCompany(); return; }
    } catch (e) { if (e instanceof ApiError && e.status === 401) { handleError(e); return; } }
    setCompanyId(null); setPermissions(new Set()); setAnyPermissions(new Set()); setRoleGrants([]); setBranchPermissions(new Map());   // nothing of the previous company survives into company selection (as changeCompany)
    await loadCompanies();
    setPhase("needCompany");
  }, [api, companyId, enterCompany, handleError, loadCompanies]);

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
    setAnyPermissions(new Set());
    setRoleGrants([]);
    setBranchPermissions(new Map());
    try { await loadCompanies(); } catch (e) { if (handleError(e)) return; }
    setPhase("needCompany");
  }, [api, handleError, loadCompanies]);

  const refreshCompanies = useCallback(async () => {
    try { await loadCompanies(); } catch (e) { handleError(e); }
  }, [handleError, loadCompanies]);

  const value = useMemo<AuthValue>(() => ({
    phase, user, companies, companyId, notice, api, login, mfa, submitMfaCode, beginEnrolment, confirmEnrolment, cancelMfa, adoptEnrolledSession, logout, chooseCompany, changeCompany, refreshCompanies, handleError,
    can: (p) => permissions.has(p),
    canAny: (p) => anyPermissions.has(p),
    canOnBranch: (p, branchId) => permissions.has(p) || (branchPermissions.get(branchId)?.has(p) ?? false),
    hasRoleOnBranch: (r, branchId) => roleGrants.some((g) => g.roleId === r && (g.scope === "company" || (g.scope === "branch" && g.scopeId === branchId))),
    hasRoleOnCostCenter: (r) => roleGrants.some((g) => g.roleId === r && g.scope === "costcenter"),
    companyName: displayCompanyName(companies.find((c) => c.id === companyId), locale),
  }), [phase, user, companies, companyId, notice, api, login, mfa, submitMfaCode, beginEnrolment, confirmEnrolment, cancelMfa, adoptEnrolledSession, logout, chooseCompany, changeCompany, refreshCompanies, handleError, permissions, anyPermissions, branchPermissions, roleGrants, locale]);

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthValue {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error("useAuth must be used inside <AuthProvider>");
  return ctx;
}
