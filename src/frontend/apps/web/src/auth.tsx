import { ApiError, createSessionGate, gateClient, type AccessMe, type ApiClient, type CompanySummary, type EnrolmentSecret, type LoginStep, type SessionGate, type SessionUser } from "@hasm/api-client";
import { useI18n } from "@hasm/ui";
import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { flushSync } from "react-dom";

/**
 * Session state lives ONLY in memory. The session itself is the HttpOnly Secure cookie (web) or an in-memory bearer token (desktop); the
 * server is the source of truth and is asked on every start (`/auth/status`). Nothing sensitive (token, password, permissions) is ever
 * written to localStorage/sessionStorage; the only stored value is the non-sensitive UI language.
 */
export type Phase = "loading" | "unreachable" | "anonymous" | "mfa" | "needCompany" | "ready";
/** The unfinished second-factor step of a login (Phase 4.1). Held in memory only; the challenge is single use, bound to this client and expires in five minutes. */
export interface MfaFlow { kind: LoginStep["kind"]; challenge: string | null; secret: EnrolmentSecret | null }
/** "resume_failed" (D-236): the sign-in succeeded but the company could not be re-entered for a reason that is NOT a refusal (no answer, a server error); still locked, the input is kept, the user signs in again. */
export type Notice = "session_expired" | "logged_out" | "resume_failed" | null;

/**
 * What the unsaved-changes registry (unsaved.tsx) lets the session provider do: ask whether anything is unsaved when the session ends, and forget everything when the
 * preserved input is dropped. The registry holds section names only; the input itself is nothing but the React state of the mounted screens.
 */
export interface UnsavedProbe { isDirty(): boolean; clear(): void }

/** The answers that mean the company can no longer be entered by this user (forbidden, gone, or no longer selectable). */
const CANNOT_ENTER: ReadonlySet<number> = new Set([403, 404, 409]);

/** Cross-tab session events (same browser, same origin). The channel carries the bare event name and nothing else; nothing is stored. */
const SESSION_CHANNEL = "hasm-session";
type SessionEvent = "signed-out" | "signed-in";

export interface AuthValue {
  phase: Phase;
  user: SessionUser | null;
  companies: CompanySummary[];
  companyId: string | null;
  notice: Notice;
  /**
   * The client the SCREENS use. It sits behind the session gate: while the gate is closed (no confirmed session, or the session ended and the user must sign in again)
   * every call is refused locally with 401 `session_locked` and nothing is sent. The provider itself signs in through the ungated client.
   */
  api: ApiClient;
  /**
   * D-236: the session ended while something was unsaved. The application shell stays MOUNTED (its React state is the preserved input: memory only, never written
   * anywhere) but is hidden and inert, and the user is asked to sign in again. The input comes back only when the SAME user (the id the server returns) signs in and
   * the SAME company can be entered again; a different user, a company that can no longer be entered, a sign-out here or in another tab, or "discard" unmounts the
   * shell first, which destroys the input.
   */
  locked: boolean;
  /** "Discard and sign out" on the sign-in-again dialog: drops the preserved input and ends up on the normal sign-in page. */
  discardLocked(): Promise<void>;
  /** Changes whenever the shell must be a NEW instance (nothing of an earlier user or company may survive in component state). */
  shellEpoch: number;
  /** The unsaved-changes registry registers itself here (see UnsavedProbe). */
  attachUnsaved(probe: UnsavedProbe | null): void;
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

export function AuthProvider({ api, bridge, gate: givenGate, children }: { api: ApiClient; bridge?: SessionBridge; gate?: SessionGate; children: ReactNode }) {
  const { locale } = useI18n();
  // `api` is the UNGATED client and never leaves this provider; the screens get `gated`.
  const gate = useMemo(() => givenGate ?? createSessionGate(), [givenGate]);
  const gated = useMemo(() => gateClient(api, gate), [api, gate]);
  const [locked, setLocked] = useState(false);
  const [shellEpoch, setShellEpoch] = useState(0);
  /** The identity the preserved input belongs to (set exactly while locked). Read synchronously by the 401 listener and by the sign-in flow. */
  const lock = useRef<{ userId: string; companyId: string } | null>(null);
  const unsaved = useRef<UnsavedProbe | null>(null);
  const attachUnsaved = useCallback((probe: UnsavedProbe | null) => { unsaved.current = probe; }, []);
  const channel = useRef<BroadcastChannel | null>(null);
  const announce = useCallback((event: SessionEvent) => { try { channel.current?.postMessage(event); } catch { /* a closed channel: nothing to tell */ } }, []);
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

  // The latest values for the listeners that outlive a render (the 401 bridge, the cross-tab channel).
  const live = useRef({ phase, user, companyId });
  live.current = { phase, user, companyId };

  /** Ends the session in the UI. The gate is closed and the lock released BEFORE the state that unmounts the shell is set; with the shell goes every preserved input. */
  const endSession = useCallback((why: Notice) => {
    gate.close();
    lock.current = null;
    setLocked(false);
    setMfa(null);
    setShellEpoch((n) => n + 1);
    unsaved.current?.clear();
    setUser(null);
    setCompanies([]);
    setCompanyId(null);
    setPermissions(new Set());
    setAnyPermissions(new Set());
    setRoleGrants([]);
    setBranchPermissions(new Map());
    setNotice(why);
    setPhase("anonymous");
  }, [gate]);

  /**
   * The server ended the session (401). With unsaved input in a ready shell the application LOCKS instead of signing out (D-236): the gate closes at once, the user
   * and company the input belongs to are remembered, and nothing else changes, so the screens stay mounted with their state. Otherwise: exactly as before.
   * A 401 cannot tell an expired session from a sign-out in another tab; that case is covered by the cross-tab event below, which always ends the session.
   */
  const expire = useCallback(() => {
    if (lock.current) return;                                   // already locked: the hidden shell's refused calls and repeated 401s change nothing
    const now = live.current;
    if (now.phase === "ready" && now.user && now.companyId && unsaved.current?.isDirty()) {
      gate.close();
      lock.current = { userId: now.user.id, companyId: now.companyId };
      setMfa(null);
      setNotice(null);                                           // the dialog carries its own explanation
      setLocked(true);
      return;
    }
    endSession("session_expired");
  }, [gate, endSession]);

  const handleError = useCallback((error: unknown) => {
    // A call refused by the closed gate was never sent: it says nothing about the session. It is reported as handled and changes NOTHING, whatever the state.
    if (error instanceof ApiError && error.code === "session_locked") return true;
    if (error instanceof ApiError && error.status === 401 && error.code !== "mfa_step_up_required" && error.code !== "mfa_step_up_cancelled") {   // a stale second-factor proof is not a session end
      expire();
      return true;
    }
    return false;
  }, [expire]);

  useEffect(() => {
    if (bridge) bridge.listener = expire;
    return () => { if (bridge) bridge.listener = null; };
  }, [bridge, expire]);

  const applyAccess = useCallback((me: AccessMe) => {
    setCompanyId(me.companyId);
    setPermissions(new Set(me.grants.filter((g) => g.scope === "company").flatMap((g) => g.permissions)));
    setAnyPermissions(new Set(me.grants.flatMap((g) => g.permissions)));
    const perBranch = new Map<string, Set<string>>();
    for (const g of me.grants) if (g.scope === "branch" && g.scopeId) { const set = perBranch.get(g.scopeId) ?? new Set<string>(); g.permissions.forEach((p) => set.add(p)); perBranch.set(g.scopeId, set); }
    setBranchPermissions(perBranch);
    setRoleGrants(me.grants.map((g) => ({ roleId: g.roleId, scope: g.scope, scopeId: g.scopeId })));
  }, []);

  const enterCompany = useCallback(async () => {
    const me = await api.accessMe();
    applyAccess(me);
    setPhase("ready");
    gate.open();                                                 // a confirmed session with a company: the screens may talk to the server
  }, [api, applyAccess, gate]);

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

  /**
   * A sign-in succeeded while the shell is locked. `signedIn` is the user the SERVER returned (never the typed name).
   *  - The same user id: the remembered company is selected on the new session and the access answer is read; only when the server confirms that very company are the
   *    permissions replaced, the lock released and - last - the gate opened. A screen whose permission is gone is then replaced by the normal "no access" rendering,
   *    which unmounts it together with its input.
   *  - Another user, or a company the server REFUSES (or answers differently): "drop", and the caller drops the shell BEFORE it continues.
   * Any failure that is not such a refusal (network, 5xx, a 401 because the brand-new session is already gone): "retry", the dialog stays, still locked.
   */
  const resumeLocked = useCallback(async (signedIn: SessionUser): Promise<"resumed" | "retry" | "drop"> => {
    const held = lock.current;
    if (!held || signedIn.id !== held.userId) return "drop";
    try {
      await api.selectCompany(held.companyId);
      const me = await api.accessMe();
      if (me.companyId !== held.companyId) return "drop";
      setUser(signedIn);
      applyAccess(me);
      setMfa(null);
      setNotice(null);
      lock.current = null;
      setLocked(false);
      gate.open();
      return "resumed";
    } catch (e) {
      // Only a DEFINITE refusal by the server means "this user can no longer enter that company". Anything else (no answer, a 5xx, a throttle, a 401 because the
      // new session is already gone) says nothing about that: the lock and the input are KEPT, the second step (its challenge is spent) is left, and the dialog's
      // password form says that signing in again will retry.
      if (e instanceof ApiError && CANNOT_ENTER.has(e.status)) return "drop";
      setMfa(null);
      setNotice("resume_failed");
      return "retry";
    }
  }, [api, applyAccess, gate]);

  /**
   * Destroys the preserved input: the shell is unmounted SYNCHRONOUSLY (flushSync) while the gate is closed, and only then does the caller go on. Nothing of the old
   * shell can therefore render, or send a request, under whatever session comes next.
   */
  const dropShell = useCallback(() => {
    gate.close();
    lock.current = null;
    flushSync(() => {
      setLocked(false);
      setShellEpoch((n) => n + 1);
      setCompanyId(null);
      setPermissions(new Set());
      setAnyPermissions(new Set());
      setRoleGrants([]);
      setBranchPermissions(new Map());
      setUser(null);
      setPhase("loading");
    });
    unsaved.current?.clear();
  }, [gate]);

  const finishLogin = useCallback(async (signedIn: SessionUser) => {
    let dropped = false;
    if (lock.current) {
      const outcome = await resumeLocked(signedIn);
      if (outcome !== "drop") { announce("signed-in"); return; }
      dropShell();
      dropped = true;
    }
    announce("signed-in");
    setMfa(null);
    setUser(signedIn);
    setNotice(null);
    try {
      const list = (await api.listCompanies()).companies;
      setCompanies(list);
      if (!(await autoEnterSole(list))) setPhase("needCompany");
    } catch (e) {
      if (!dropped) throw e;                                     // an ordinary sign-in: the form is still there and shows the error, as before
      // The shell was just dropped (phase "loading") and the sign-in form is gone with the dialog: never stay there. A 401 ends the session; anything else shows
      // the "service unreachable" page with its retry.
      if (e instanceof ApiError && e.status === 401) endSession("session_expired"); else setPhase("unreachable");
    }
  }, [api, autoEnterSole, resumeLocked, dropShell, announce, endSession]);

  const login = useCallback(async (loginName: string, password: string) => {
    const result = await api.login(loginName, password); // throws ApiError: the form shows the mapped message
    if (result.step) {                                   // a second factor, an enrolment or a recovery stands between the password and a session
      setMfa({ kind: result.step.kind, challenge: result.step.kind === "mfa_recovery_required" ? null : result.step.challenge, secret: null });
      if (!lock.current) setPhase("mfa");                // while locked the phase stays "ready" (the shell must stay mounted); the dialog shows the second step from `mfa`
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

  const cancelMfa = useCallback(() => { setMfa(null); if (!lock.current) setPhase("anonymous"); }, []);   // while locked: back to the password form of the dialog

  const adoptEnrolledSession = useCallback(async (enrolled: SessionUser) => {
    setUser(enrolled);
    const previous = companyId;
    try {
      if (previous) { await api.selectCompany(previous); await enterCompany(); return; }
    } catch (e) { if (e instanceof ApiError && e.status === 401) { handleError(e); return; } }
    gate.close();
    setShellEpoch((n) => n + 1);
    setCompanyId(null); setPermissions(new Set()); setAnyPermissions(new Set()); setRoleGrants([]); setBranchPermissions(new Map());   // nothing of the previous company survives into company selection (as changeCompany)
    setPhase("needCompany");                                     // at once (the invariant above); the list refreshes behind it
    try { await loadCompanies(); } catch (e) { handleError(e); }
  }, [api, companyId, enterCompany, gate, handleError, loadCompanies]);

  const logout = useCallback(async () => {
    try { await api.logout(); } catch { /* the server session may already be gone; the local state is cleared regardless */ }
    endSession("logged_out");
    announce("signed-out");
  }, [api, endSession, announce]);

  const discardLocked = useCallback(async () => {
    endSession("logged_out");                                    // first: the input is gone whatever the server answers
    announce("signed-out");
    try { await api.logout(); } catch { /* the session has already ended */ }
  }, [api, endSession, announce]);

  // Cross-tab events (Web: the tabs of one browser share the session cookie; Tauri: one window, so nothing ever arrives). "Cleared on logout" must also hold when the
  // sign-out happens in ANOTHER tab, and a 401 alone cannot tell that from an expiry. "signed-out": this tab ends its session at once, locked or not. "signed-in": the
  // shared cookie may now belong to someone else, so the server is asked who is signed in; anyone but this tab's user ends the session here. Where BroadcastChannel
  // does not exist nothing is announced: preserved input then lasts until the sign-in in this tab, where the user-id check still applies.
  useEffect(() => {
    if (typeof BroadcastChannel === "undefined") return undefined;
    const ch = new BroadcastChannel(SESSION_CHANNEL);
    channel.current = ch;
    ch.onmessage = (message: MessageEvent<unknown>) => {
      const now = live.current;
      const mine = lock.current?.userId ?? now.user?.id ?? null;
      if (message.data === "signed-out") {
        if (mine !== null) endSession("logged_out");
      } else if (message.data === "signed-in" && mine !== null) {
        void api.status().then((status) => {
          const current = lock.current?.userId ?? live.current.user?.id ?? null;
          if (current !== null && status.authenticated && status.user && status.user.id !== current) endSession("session_expired");
        }).catch(() => { /* unreachable or already ended: the next call of this tab decides */ });
      }
    };
    return () => { channel.current = null; ch.close(); };
  }, [api, endSession]);

  const chooseCompany = useCallback(async (id: string) => {
    await api.selectCompany(id);
    await enterCompany();
  }, [api, enterCompany]);

  // Invariant: outside the lock of D-236 a MOUNTED shell never sits behind a CLOSED gate. Whoever closes the gate leaves the phase "ready" in the same step.
  const changeCompany = useCallback(async () => {
    try { await api.clearCompany(); } catch (e) { if (handleError(e)) return; }
    gate.close();                                                // no company: nothing for a screen to ask; the next enterCompany opens it again
    setShellEpoch((n) => n + 1);
    setCompanyId(null);
    setPermissions(new Set());
    setAnyPermissions(new Set());
    setRoleGrants([]);
    setBranchPermissions(new Map());
    setPhase("needCompany");                                     // at once: the selection shows the list it already has while the fresh one loads
    try { await loadCompanies(); } catch (e) { handleError(e); }
  }, [api, gate, handleError, loadCompanies]);

  const refreshCompanies = useCallback(async () => {
    try { await loadCompanies(); } catch (e) { handleError(e); }
  }, [handleError, loadCompanies]);

  const value = useMemo<AuthValue>(() => ({
    phase, user, companies, companyId, notice, api: gated, locked, discardLocked, shellEpoch, attachUnsaved, login, mfa, submitMfaCode, beginEnrolment, confirmEnrolment, cancelMfa, adoptEnrolledSession, logout, chooseCompany, changeCompany, refreshCompanies, handleError,
    can: (p) => permissions.has(p),
    canAny: (p) => anyPermissions.has(p),
    canOnBranch: (p, branchId) => permissions.has(p) || (branchPermissions.get(branchId)?.has(p) ?? false),
    hasRoleOnBranch: (r, branchId) => roleGrants.some((g) => g.roleId === r && (g.scope === "company" || (g.scope === "branch" && g.scopeId === branchId))),
    hasRoleOnCostCenter: (r) => roleGrants.some((g) => g.roleId === r && g.scope === "costcenter"),
    companyName: displayCompanyName(companies.find((c) => c.id === companyId), locale),
  }), [phase, user, companies, companyId, notice, gated, locked, discardLocked, shellEpoch, attachUnsaved, login, mfa, submitMfaCode, beginEnrolment, confirmEnrolment, cancelMfa, adoptEnrolledSession, logout, chooseCompany, changeCompany, refreshCompanies, handleError, permissions, anyPermissions, branchPermissions, roleGrants, locale]);

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthValue {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error("useAuth must be used inside <AuthProvider>");
  return ctx;
}
