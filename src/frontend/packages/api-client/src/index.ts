export interface HealthResponse {
  status: string;
  service: string;
  version: string;
}

export interface ApiErrorDetails {
  fields?: { field: string; code: string }[];
  currentRevision?: number;
  retryAfterSeconds?: number;
}

/** A failed API call. <c>code</c> is the server's short error code (never an exception text). */
export class ApiError extends Error {
  readonly status: number;
  readonly code: string;
  readonly details: ApiErrorDetails;

  constructor(status: number, code: string, details: ApiErrorDetails = {}) {
    super(`${status} ${code}`);
    this.name = "ApiError";
    this.status = status;
    this.code = code;
    this.details = details;
  }
}

export interface SessionUser {
  id: string;
  login: string;
  displayName: string;
  isIdentityAdministrator: boolean;
}
export interface AuthStatus {
  authenticated: boolean;
  user?: SessionUser;
  session?: { client: string; idleExpiresAtUtc: string; absoluteExpiresAtUtc: string };
}
export interface CompanySummary {
  id: string;
  nameAr: string | null;
  nameEn: string | null;
}
export interface Grant {
  roleId: string;
  scope: string;
  scopeId: string | null;
  permissions: string[];
}
export interface AccessMe {
  companyId: string;
  grants: Grant[];
}

export interface ProfileFields {
  nameAr: string | null;
  nameEn: string | null;
  legalName: string | null;
  commercialRegistrationNo: string | null;
  taxRegistrationNo: string | null;
  countryCode: string;
  addressLine1: string | null;
  addressLine2: string | null;
  city: string | null;
  region: string | null;
  postalCode: string | null;
  phone: string | null;
  email: string | null;
  website: string | null;
}
export interface ProfileResponse {
  profile: ProfileFields | null;
  revision: number;
  updatedAtUtc: string | null;
  projection: "none" | "synced" | "pending";
}

export interface SettingsFields {
  defaultLanguage: string;
  dateFormat: string;
  numeralSystem: string;
  timeZone: string | null;
}
export interface SettingsResponse {
  settings: SettingsFields;
  revision: number;
  exists: boolean;
  setupComplete: boolean;
  updatedAtUtc: string | null;
  options: { languages: string[]; dateFormats: string[]; numeralSystems: string[] };
}

export interface CompanyCurrency {
  code: string;
  nameEn: string;
  minorUnits: number | null;
  isBase: boolean;
  enabled: boolean;
  revision: number;
}
export interface CurrenciesResponse {
  currencies: CompanyCurrency[];
  baseCurrency: string | null;
  baseCurrencySelected: boolean;
  baseCurrencyLock: { locked: boolean; checksImplemented: boolean; unimplementedDomains: string[] };
}
export interface CurrencyReference {
  code: string;
  numericCode: string;
  nameEn: string;
  minorUnits: number | null;
  active: boolean;
  datasetVersion: number;
}
export interface HistoryEntry {
  id: number;
  entity: string;
  key: string;
  revision: number;
  actorUserId: string;
  changedAtUtc: string;
  changes: Record<string, { old?: unknown; new?: unknown; changed?: boolean }>;
  auditSeq: number;
}
export interface EntitlementsResponse {
  edition: string;
  restrictions: { key: string; limit: number | null; enforced: boolean; note: string }[];
}


export interface Branch {
  id: string;
  code: string;
  nameAr: string | null;
  nameEn: string | null;
  isActive: boolean;
  isMain: boolean;
  /** Advances on every deactivation and reactivation (branch-scoped access assignments depend on it). */
  activationEpoch: number;
  /** True for the main branch created by the system when the company was provisioned. */
  seeded: boolean;
  addressLine1: string | null;
  addressLine2: string | null;
  city: string | null;
  region: string | null;
  postalCode: string | null;
  phone: string | null;
  email: string | null;
  website: string | null;
  revision: number;
  createdAtUtc: string;
  updatedAtUtc: string;
}
/** The editable fields of a branch (the code is sent on create only and can never change afterwards). */
export type BranchInput = Pick<Branch, "nameAr" | "nameEn" | "addressLine1" | "addressLine2" | "city" | "region" | "postalCode" | "phone" | "email" | "website"> & { code?: string };
export interface BranchesResponse {
  branches: Branch[];
  /** Edition limit of branches ever created, and how many exist (inactive ones count). */
  limit: number | null;
  everCreated: number | null;
  /** What this caller may do. The server authorizes every action again; this only shows or hides controls. */
  can: { create: boolean; activate: boolean; designate: boolean; update: string[] };
}

export interface Warehouse {
  id: string;
  /** The branch the warehouse belongs to; fixed for life. */
  branchId: string;
  code: string;
  nameAr: string | null;
  nameEn: string | null;
  isActive: boolean;
  /** Advances on every deactivation and reactivation (warehouse-scoped access assignments depend on it). */
  activationEpoch: number;
  addressLine1: string | null;
  addressLine2: string | null;
  city: string | null;
  region: string | null;
  postalCode: string | null;
  phone: string | null;
  email: string | null;
  website: string | null;
  revision: number;
  createdAtUtc: string;
  updatedAtUtc: string;
}
/** The editable fields of a warehouse (the code and the branch are sent on create only and can never change afterwards). */
export type WarehouseInput = Pick<Warehouse, "nameAr" | "nameEn" | "addressLine1" | "addressLine2" | "city" | "region" | "postalCode" | "phone" | "email" | "website"> & { code?: string; branchId?: string };
/** Minimal branch facts for the branch picker and the branch column (never address or contact data). */
export interface WarehouseBranchRef { id: string; code: string; nameAr: string | null; nameEn: string | null; isActive: boolean }
export interface WarehousesResponse {
  warehouses: Warehouse[];
  branches: WarehouseBranchRef[];
  /** Edition limit of warehouses ever created, and how many exist (inactive ones count). */
  limit: number | null;
  everCreated: number | null;
  /** What this caller may do. The server authorizes every action again; this only shows or hides controls. */
  can: { create: boolean; activate: boolean; update: string[] };
}

export type FiscalPeriodStatus = "open" | "soft_closed" | "final_closed";
export interface FiscalPeriod {
  id: string;
  yearId: string;
  number: number;
  /** Calendar dates (yyyy-MM-dd) in the company's business time zone. */
  startDate: string;
  endDate: string;
  status: FiscalPeriodStatus;
  statusChangedAtUtc: string | null;
  revision: number;
}
export interface FiscalYear {
  id: string;
  name: string;
  startDate: string;
  endDate: string;
  periodType: "monthly" | "quarterly";
  /** The business time zone the calendar was created under (informational snapshot). */
  timeZone: string;
  closed: boolean;
  closedAtUtc: string | null;
  revision: number;
  createdAtUtc: string;
  periods: FiscalPeriod[];
}
export interface FiscalYearInput { name: string; startDate: string; periodType: "monthly" | "quarterly" }
export interface FiscalYearsResponse {
  years: FiscalYear[];
  /** Today in the company's business time zone; null until the time zone is chosen (a year cannot be created before that). */
  today: string | null;
  /** False while the modules whose pending items must block a close do not exist: closing records the state only and is not "verified". */
  closeChecksImplemented: boolean;
  /** What this caller may do. The server authorizes every action again; this only shows or hides controls. */
  can: { create: boolean; closeYear: boolean; softClose: boolean; reopen: boolean; finalClose: boolean; requestReopen: boolean; approveReopen: boolean; viewRequests: boolean };
}
export type FiscalReopenStatus = "pending" | "approved" | "denied" | "cancelled";
/** A request for the exceptional reopening of a Final Closed period or a closed year. Only a different authorized user can approve it. */
export interface FiscalReopenRequest {
  id: string;
  target: "period" | "year";
  targetId: string;
  yearName: string;
  periodNumber: number | null;
  startDate: string | null;
  endDate: string | null;
  /** The revision of the target the request was made for; approval is refused when the target has moved on. */
  expectedRevision: number;
  reason: string;
  status: FiscalReopenStatus;
  requestedAtUtc: string;
  decidedAtUtc: string | null;
  decisionNote: string | null;
  revision: number;
  /** The caller made this request. */
  mine: boolean;
  /** The server says the caller may approve or deny it now (never their own request). */
  canApprove: boolean;
  /** The caller may withdraw it (their own pending request). */
  canCancel: boolean;
}

export type AccountType = "asset" | "liability" | "equity" | "revenue" | "expense";
export type AccountNature = "debit" | "credit";
export type AccountCurrencyPolicy = "base" | "any" | "specific";
export interface Account {
  /** The stable identifier: everything refers to it. The code below may be changed (audited). */
  id: string;
  code: string;
  nameAr: string | null;
  nameEn: string | null;
  parentId: string | null;
  type: AccountType;
  nature: AccountNature;
  statement: "balance_sheet" | "profit_and_loss";
  /** A group (parent) account is never postable; only an active account that is not a group is. */
  isGroup: boolean;
  isActive: boolean;
  postable: boolean;
  hasChildren: boolean;
  currencyPolicy: AccountCurrencyPolicy;
  currencyCode: string | null;
  template: string | null;
  revision: number;
  createdAtUtc: string;
  updatedAtUtc: string;
}
export interface AccountInput {
  /** Absent or blank = assigned automatically (when the company setting allows it). */
  code?: string | null;
  parentId?: string | null;
  nameAr: string | null;
  nameEn: string | null;
  /** Required for a top-level account; inherited from the parent otherwise. */
  type?: AccountType | null;
  nature?: AccountNature | null;
  isGroup: boolean;
  currencyPolicy?: AccountCurrencyPolicy | null;
  currencyCode?: string | null;
}
export type AccountDetailsInput = Pick<AccountInput, "nameAr" | "nameEn" | "nature" | "isGroup" | "currencyPolicy" | "currencyCode">;
export interface ChartSettings { segmentWidth: number; autoCodes: boolean; revision: number; configured: boolean }
export interface ChartTemplateInfo { key: string; nameAr: string; nameEn: string; accounts: number }
export interface AccountsResponse {
  accounts: Account[];
  settings: ChartSettings | null;
  /** Only the generic template exists; business-type templates and Excel import are outstanding Release 1 requirements. */
  templates: ChartTemplateInfo[];
  /** False until the General Ledger exists: whether an account has postings cannot be checked yet, and the UI must not present "no history" as verified. */
  usageChecksImplemented: boolean;
  /** What this caller may do. The server authorizes every action again; this only shows or hides controls. */
  can: { create: boolean; update: boolean; activate: boolean; recode: boolean; restructure: boolean; configure: boolean };
}
export type ClientKind = "web" | "desktop";

export interface ApiClientOptions {
  /**
   * "web": the HttpOnly Secure session cookie (sent with credentials). "desktop": an opaque bearer token kept ONLY in memory (never in
   * localStorage/sessionStorage); a restart of the desktop app asks for the password again.
   */
  client?: ClientKind;
  /** Called when an authenticated call is answered 401 (the session ended or expired). */
  onUnauthenticated?: () => void;
}

/** Typed HTTP client for the HASM Backend API. It never talks to the database. */
export function createApiClient(baseUrl: string, fetchImpl: typeof fetch = (...args) => fetch(...args), options: ApiClientOptions = {}) {
  const root = baseUrl.replace(/\/+$/, "");
  const kind: ClientKind = options.client ?? "web";
  let bearer: string | null = null;

  async function request<T>(method: string, path: string, opts: { body?: unknown; ifMatch?: number; anonymous?: boolean } = {}): Promise<T> {
    const headers: Record<string, string> = { Accept: "application/json" };
    if (method !== "GET") headers["X-Hasm-Request"] = "1"; // anti-CSRF header required by the API on every state-changing call
    if (opts.body !== undefined) headers["Content-Type"] = "application/json";
    if (opts.ifMatch !== undefined) headers["If-Match"] = `"${opts.ifMatch}"`;
    if (kind === "desktop" && bearer) headers["Authorization"] = `Bearer ${bearer}`;
    const response = await fetchImpl(`${root}${path}`, {
      method,
      headers,
      credentials: kind === "web" ? "include" : "omit",
      cache: "no-store",
      body: opts.body === undefined ? undefined : JSON.stringify(opts.body),
    });
    if (!response.ok) {
      let payload: { error?: string; fields?: { field: string; code: string }[]; currentRevision?: number } = {};
      try {
        payload = (await response.json()) as typeof payload;
      } catch {
        /* non-JSON error body */
      }
      const retry = Number(response.headers?.get?.("Retry-After"));
      if (response.status === 401 && !opts.anonymous) options.onUnauthenticated?.();
      throw new ApiError(response.status, payload.error ?? "request_failed", {
        fields: payload.fields,
        currentRevision: payload.currentRevision,
        retryAfterSeconds: Number.isFinite(retry) && retry > 0 ? retry : undefined,
      });
    }
    if (response.status === 204) return undefined as T;
    return (await response.json()) as T;
  }

  const get = <T>(path: string) => request<T>("GET", path);
  const enc = encodeURIComponent;

  return {
    getHealth: () => get<HealthResponse>("/api/v1/health"),

    async login(login: string, password: string): Promise<{ user: SessionUser }> {
      const result = await request<{ user: SessionUser; token?: string | null }>("POST", "/api/v1/auth/login", {
        body: { login, password, client: kind },
        anonymous: true,
      });
      bearer = kind === "desktop" ? (result.token ?? null) : null;
      return { user: result.user };
    },
    async logout(): Promise<void> {
      try {
        await request<void>("POST", "/api/v1/auth/logout", { anonymous: true });
      } finally {
        bearer = null;
      }
    },
    status: () => request<AuthStatus>("GET", "/api/v1/auth/status", { anonymous: true }),

    listCompanies: () => get<{ companies: CompanySummary[] }>("/api/v1/companies"),
    selectCompany: (companyId: string) => request<{ companyId: string }>("POST", "/api/v1/session/company", { body: { companyId } }),
    clearCompany: () => request<void>("DELETE", "/api/v1/session/company"),
    accessMe: () => get<AccessMe>("/api/v1/access/me"),

    getProfile: () => get<ProfileResponse>("/api/v1/company/profile"),
    putProfile: (profile: ProfileFields, revision: number) => request<ProfileResponse>("PUT", "/api/v1/company/profile", { body: profile, ifMatch: revision }),
    syncDisplayName: () => request<ProfileResponse>("POST", "/api/v1/company/profile/display-name-sync"),

    getSettings: () => get<SettingsResponse>("/api/v1/company/settings"),
    putSettings: (settings: SettingsFields, revision: number) => request<SettingsResponse>("PUT", "/api/v1/company/settings", { body: settings, ifMatch: revision }),

    getCurrencies: () => get<CurrenciesResponse>("/api/v1/company/currencies"),
    getCurrencyReference: () => get<{ currencies: CurrencyReference[] }>("/api/v1/company/currencies/reference"),
    setCurrency: (code: string, state: { enabled: boolean; isBase: boolean }, revision: number) =>
      request<CurrenciesResponse>("PUT", `/api/v1/company/currencies/${enc(code)}`, { body: state, ifMatch: revision }),

    getHistory: (entity?: string, before?: number) =>
      get<{ entries: HistoryEntry[] }>(`/api/v1/company/history?limit=50${entity ? `&entity=${enc(entity)}` : ""}${before ? `&before=${before}` : ""}`),
    getEntitlements: () => get<EntitlementsResponse>("/api/v1/company/entitlements"),

    // Branch management (Phase 3.1B). Changes carry the branch revision as If-Match; the server enforces every rule.
    listBranches: () => get<BranchesResponse>("/api/v1/company/branches"),
    getBranch: (id: string) => get<Branch>(`/api/v1/company/branches/${enc(id)}`),
    createBranch: (branch: BranchInput) => request<Branch>("POST", "/api/v1/company/branches", { body: branch }),
    updateBranch: (id: string, branch: BranchInput, revision: number) =>
      request<Branch>("PUT", `/api/v1/company/branches/${enc(id)}`, { body: branch, ifMatch: revision }),
    /** The main branch can be deactivated only together with `newMainId`, another active branch (atomic on the server). */
    deactivateBranch: (id: string, revision: number, newMainId?: string) =>
      request<Branch>("POST", `/api/v1/company/branches/${enc(id)}/deactivate`, { body: newMainId ? { newMainId } : {}, ifMatch: revision }),
    activateBranch: (id: string, revision: number) => request<Branch>("POST", `/api/v1/company/branches/${enc(id)}/activate`, { ifMatch: revision }),
    makeMainBranch: (id: string, revision: number) => request<Branch>("POST", `/api/v1/company/branches/${enc(id)}/make-main`, { ifMatch: revision }),
    // Warehouse management (Phase 3.1C). Same conventions as branches: the warehouse revision is sent as If-Match; the server enforces every rule.
    listWarehouses: () => get<WarehousesResponse>("/api/v1/company/warehouses"),
    getWarehouse: (id: string) => get<Warehouse>(`/api/v1/company/warehouses/${enc(id)}`),
    createWarehouse: (warehouse: WarehouseInput) => request<Warehouse>("POST", "/api/v1/company/warehouses", { body: warehouse }),
    updateWarehouse: (id: string, warehouse: WarehouseInput, revision: number) =>
      request<Warehouse>("PUT", `/api/v1/company/warehouses/${enc(id)}`, { body: warehouse, ifMatch: revision }),
    deactivateWarehouse: (id: string, revision: number) => request<Warehouse>("POST", `/api/v1/company/warehouses/${enc(id)}/deactivate`, { ifMatch: revision }),
    activateWarehouse: (id: string, revision: number) => request<Warehouse>("POST", `/api/v1/company/warehouses/${enc(id)}/activate`, { ifMatch: revision }),
    // Fiscal calendar (Phase 3.1D). Changes carry the revision of the year or period as If-Match; the server enforces every rule.
    listFiscalYears: () => get<FiscalYearsResponse>("/api/v1/company/fiscal-years"),
    getFiscalYear: (id: string) => get<FiscalYear>(`/api/v1/company/fiscal-years/${enc(id)}`),
    createFiscalYear: (year: FiscalYearInput) => request<FiscalYear>("POST", "/api/v1/company/fiscal-years", { body: year }),
    closeFiscalYear: (id: string, revision: number) => request<FiscalYear>("POST", `/api/v1/company/fiscal-years/${enc(id)}/close`, { ifMatch: revision }),
    softCloseFiscalPeriod: (id: string, revision: number) => request<FiscalPeriod>("POST", `/api/v1/company/fiscal-periods/${enc(id)}/soft-close`, { ifMatch: revision }),
    reopenFiscalPeriod: (id: string, revision: number) => request<FiscalPeriod>("POST", `/api/v1/company/fiscal-periods/${enc(id)}/reopen`, { ifMatch: revision }),
    finalCloseFiscalPeriod: (id: string, revision: number) => request<FiscalPeriod>("POST", `/api/v1/company/fiscal-periods/${enc(id)}/final-close`, { ifMatch: revision }),
    // Exceptional reopening is an approval workflow: request (reason + the revision seen), then a DIFFERENT authorized user approves/denies; the requester may cancel.
    listFiscalReopenRequests: () => get<{ requests: FiscalReopenRequest[] }>("/api/v1/company/fiscal-reopen-requests"),
    requestFiscalPeriodReopen: (id: string, revision: number, reason: string) =>
      request<FiscalReopenRequest>("POST", `/api/v1/company/fiscal-periods/${enc(id)}/reopen-requests`, { body: { reason }, ifMatch: revision }),
    requestFiscalYearReopen: (id: string, revision: number, reason: string) =>
      request<FiscalReopenRequest>("POST", `/api/v1/company/fiscal-years/${enc(id)}/reopen-requests`, { body: { reason }, ifMatch: revision }),
    approveFiscalReopen: (id: string, revision: number) => request<FiscalReopenRequest>("POST", `/api/v1/company/fiscal-reopen-requests/${enc(id)}/approve`, { ifMatch: revision }),
    denyFiscalReopen: (id: string, revision: number, note?: string) =>
      request<FiscalReopenRequest>("POST", `/api/v1/company/fiscal-reopen-requests/${enc(id)}/deny`, { body: note ? { note } : {}, ifMatch: revision }),
    cancelFiscalReopen: (id: string, revision: number) => request<FiscalReopenRequest>("POST", `/api/v1/company/fiscal-reopen-requests/${enc(id)}/cancel`, { ifMatch: revision }),
    // Chart of Accounts (Phase 3.2A). Changes carry the account (or settings) revision as If-Match; the server enforces every rule.
    listAccounts: () => get<AccountsResponse>("/api/v1/company/accounts"),
    getAccount: (id: string) => get<Account>(`/api/v1/company/accounts/${enc(id)}`),
    createAccount: (account: AccountInput) => request<Account>("POST", "/api/v1/company/accounts", { body: account }),
    updateAccount: (id: string, details: AccountDetailsInput, revision: number) =>
      request<Account>("PUT", `/api/v1/company/accounts/${enc(id)}`, { body: details, ifMatch: revision }),
    recodeAccount: (id: string, code: string, revision: number) =>
      request<Account>("POST", `/api/v1/company/accounts/${enc(id)}/recode`, { body: { code }, ifMatch: revision }),
    moveAccount: (id: string, parentId: string | null, revision: number) =>
      request<Account>("POST", `/api/v1/company/accounts/${enc(id)}/move`, { body: { parentId }, ifMatch: revision }),
    deactivateAccount: (id: string, revision: number) => request<Account>("POST", `/api/v1/company/accounts/${enc(id)}/deactivate`, { ifMatch: revision }),
    activateAccount: (id: string, revision: number) => request<Account>("POST", `/api/v1/company/accounts/${enc(id)}/activate`, { ifMatch: revision }),
    putChartSettings: (settings: { segmentWidth: number; autoCodes: boolean }, revision: number) =>
      request<ChartSettings>("PUT", "/api/v1/company/accounts/settings", { body: settings, ifMatch: revision }),
    applyChartTemplate: (template: string) => request<{ accounts: Account[] }>("POST", "/api/v1/company/accounts/apply-template", { body: { template } }),
  };
}

export type ApiClient = ReturnType<typeof createApiClient>;
