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

// ---------------------------------------------------------------- General Ledger foundation and journal (Phase 3.2B)
/** Amounts, rates and thresholds travel as exact decimal STRINGS ("100.0000"); they are never converted to floating point for any calculation. */
export interface NumberingRule { docType: string; prefix: string; separator: string; width: number; perBranch: boolean; perFiscalYear: boolean; revision: number; configured: boolean; numbersIssued: boolean }
export interface NumberingResponse { rules: NumberingRule[]; can: { configure: boolean } }
export type NumberingRuleInput = Pick<NumberingRule, "prefix" | "separator" | "width" | "perBranch" | "perFiscalYear">;

export interface ExchangeRate { id: number; currencyCode: string; rate: string; validFrom: string; enteredAtUtc: string; enteredBy: string; note: string | null }
export interface ExchangeRateCurrency { currencyCode: string; nameEn: string | null; minorUnits: number | null; latestId: number; latest: ExchangeRate | null }
export interface ExchangeRatesOverview {
  /** Always "base_per_foreign_unit": the number of base-currency units for ONE unit of the foreign currency. */
  direction: string;
  baseCurrency: string;
  baseMinorUnits: number | null;
  currencies: ExchangeRateCurrency[];
  can: { create: boolean };
}
export interface ExchangeRateInput { currencyCode: string; rate: string | number; validFrom: string; note?: string | null }
export interface AppliedRateInfo { rateId: number; currencyCode: string; rate: string; validFrom: string }

export interface CostCenter {
  id: string; code: string; nameAr: string | null; nameEn: string | null; parentId: string | null; branchId: string | null;
  isGroup: boolean; isActive: boolean; canReceiveAllocations: boolean; hasChildren: boolean; revision: number; createdAtUtc: string; updatedAtUtc: string;
}
export interface CostCentersResponse { costCenters: CostCenter[]; can: { create: boolean; update: boolean; activate: boolean; recode: boolean; restructure: boolean } }
export interface CostCenterInput { code?: string | null; parentId?: string | null; nameAr: string | null; nameEn: string | null; branchId?: string | null; isGroup: boolean }
export type CostCenterDetailsInput = Pick<CostCenterInput, "nameAr" | "nameEn" | "branchId" | "isGroup">;

export type PolicySource = "manual" | "automatic" | "any";
export interface ApprovalPolicy {
  id: string; docType: string; source: PolicySource; branchId: string | null; minAmount: string; requiredApprovals: number;
  nameAr: string | null; nameEn: string | null; isActive: boolean; revision: number; createdAtUtc: string; updatedAtUtc: string;
}
export interface ApprovalPoliciesResponse { policies: ApprovalPolicy[]; documentTypes: string[]; can: { create: boolean; update: boolean; activate: boolean } }
export interface ApprovalPolicyInput { docType: string; source: PolicySource; branchId?: string | null; minAmount: string | number; requiredApprovals: number; nameAr: string | null; nameEn: string | null }
export type ApprovalPolicyDetailsInput = Omit<ApprovalPolicyInput, "docType">;

export type JournalStatus = "draft" | "pending_approval" | "approved" | "posted" | "reversed" | "cancelled";
export interface JournalHeader {
  id: string; number: string | null; status: JournalStatus; type: "normal" | "reversal" | "adjusting"; source: "manual" | "automatic"; sourceKey: string | null;
  entryDate: string; description: string | null; branchId: string; baseCurrency: string; baseTotal: string; revision: number; createdBy: string; createdAtUtc: string; updatedAtUtc: string;
  submittedBy: string | null; submittedAtUtc: string | null; postedBy: string | null; postedAtUtc: string | null; periodId: string | null; postedInSoftClosedPeriod: boolean;
  reversesId: string | null; adjustsId: string | null;
}
export interface JournalAllocation { costCenterId: string; amount: string; baseAmount: string }
export interface JournalLine {
  id: string; lineNo: number; accountId: string; description: string | null; currency: string; debit: string; credit: string; rate: string; rateId: number | null;
  rateOverrideReason: string | null; baseDebit: string; baseCredit: string; branchId: string; isRounding: boolean; reversesLineId: string | null; allocations: JournalAllocation[];
}
export interface JournalApprovalDecision { id: number; documentRevision: number; approver: string; approved: boolean; decidedAtUtc: string; comment: string | null }
export interface JournalEventInfo { id: number; kind: string; actor: string; atUtc: string; revision: number; detail: string | null }
export interface JournalEntry {
  entry: JournalHeader; balanced: boolean; baseDebitTotal: string; baseCreditTotal: string; requiredApprovals: number;
  lines: JournalLine[]; approvals: JournalApprovalDecision[]; events: JournalEventInfo[];
}
export interface JournalListResponse {
  entries: JournalHeader[];
  can: { create: boolean; approve: boolean; post: boolean; reverse: boolean; rateOverride: boolean; postInSoftClosed: boolean };
}
export interface JournalAllocationInput { costCenterId: string; amount: string | number }
export interface JournalLineInput {
  accountId: string; description?: string | null; currency?: string | null; debit?: string | number | null; credit?: string | number | null; branchId?: string | null;
  rateOverride?: string | number | null; rateOverrideReason?: string | null; allocations?: JournalAllocationInput[] | null;
}
export interface JournalInput { entryDate: string; description?: string | null; branchId: string; lines: JournalLineInput[]; adjustsId?: string | null }
export interface LedgerSettings { roundingAccountId: string | null; revision: number; configured: boolean }

export interface TrialBalanceRowInfo { accountId: string; code: string; nameAr: string | null; nameEn: string | null; parentId: string | null; level: number; isGroup: boolean; opening: string; debit: string; credit: string; closing: string }
export interface TrialBalanceResponse { from: string; to: string; baseCurrency: string; balanced: boolean; totals: { opening: string; debit: string; credit: string; closing: string }; rows: TrialBalanceRowInfo[] }
export interface GeneralLedgerLineInfo {
  entryId: string; number: string; entryDate: string; entryType: string; entryDescription: string | null; lineDescription: string | null; accountId: string; currency: string;
  debit: string; credit: string; rate: string; rateId: number | null; baseDebit: string; baseCredit: string; branchId: string; isRounding: boolean; runningBalance: string;
}
export interface GeneralLedgerResponse {
  from: string; to: string; baseCurrency: string; accountId: string; includesDescendants: boolean; opening: string; totalDebit: string; totalCredit: string; closing: string;
  totalLines: number; offset: number; lines: GeneralLedgerLineInfo[];
}
export interface ReportQuery { from: string; to: string; branchId?: string; costCenterId?: string }
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

  /** Downloads a file (CSV report). Same transport rules as every other call; a failure is reported with the server's short error code. */
  async function download(path: string): Promise<Blob> {
    const headers: Record<string, string> = { Accept: "text/csv" };
    if (kind === "desktop" && bearer) headers["Authorization"] = `Bearer ${bearer}`;
    const response = await fetchImpl(`${root}${path}`, { method: "GET", headers, credentials: kind === "web" ? "include" : "omit", cache: "no-store" });
    if (!response.ok) {
      let payload: { error?: string } = {};
      try { payload = (await response.json()) as typeof payload; } catch { /* non-JSON error body */ }
      if (response.status === 401) options.onUnauthenticated?.();
      throw new ApiError(response.status, payload.error ?? "request_failed");
    }
    return await response.blob();
  }
  const reportQuery = (q: { from: string; to: string; branchId?: string; costCenterId?: string }) =>
    `from=${enc(q.from)}&to=${enc(q.to)}${q.branchId ? `&branchId=${enc(q.branchId)}` : ""}${q.costCenterId ? `&costCenterId=${enc(q.costCenterId)}` : ""}`;

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
    // Document numbering, exchange rates, cost centers and approval policies (Phase 3.2B). Changes carry the revision (or the currency's latest rate id) as If-Match.
    getNumbering: () => get<NumberingResponse>("/api/v1/company/numbering"),
    putNumberingRule: (docType: string, rule: NumberingRuleInput, revision: number) =>
      request<NumberingRule>("PUT", `/api/v1/company/numbering/${enc(docType)}`, { body: rule, ifMatch: revision }),
    getExchangeRates: () => get<ExchangeRatesOverview>("/api/v1/company/exchange-rates"),
    getExchangeRateHistory: (currency?: string, from?: string, to?: string) =>
      get<{ rates: ExchangeRate[] }>(`/api/v1/company/exchange-rates/history?limit=200${currency ? `&currency=${enc(currency)}` : ""}${from ? `&from=${enc(from)}` : ""}${to ? `&to=${enc(to)}` : ""}`),
    resolveExchangeRate: (currency: string, date: string) => get<AppliedRateInfo>(`/api/v1/company/exchange-rates/resolve?currency=${enc(currency)}&date=${enc(date)}`),
    /** `latestId` is the currency's latest entry id from the overview (0 when it has none): a concurrent entry makes this fail with 412. */
    enterExchangeRate: (rate: ExchangeRateInput, latestId: number) => request<ExchangeRate>("POST", "/api/v1/company/exchange-rates", { body: rate, ifMatch: latestId }),
    listCostCenters: () => get<CostCentersResponse>("/api/v1/company/cost-centers"),
    createCostCenter: (c: CostCenterInput) => request<CostCenter>("POST", "/api/v1/company/cost-centers", { body: c }),
    updateCostCenter: (id: string, c: CostCenterDetailsInput, revision: number) => request<CostCenter>("PUT", `/api/v1/company/cost-centers/${enc(id)}`, { body: c, ifMatch: revision }),
    recodeCostCenter: (id: string, code: string, revision: number) => request<CostCenter>("POST", `/api/v1/company/cost-centers/${enc(id)}/recode`, { body: { code }, ifMatch: revision }),
    moveCostCenter: (id: string, parentId: string | null, revision: number) => request<CostCenter>("POST", `/api/v1/company/cost-centers/${enc(id)}/move`, { body: { parentId }, ifMatch: revision }),
    deactivateCostCenter: (id: string, revision: number) => request<CostCenter>("POST", `/api/v1/company/cost-centers/${enc(id)}/deactivate`, { ifMatch: revision }),
    activateCostCenter: (id: string, revision: number) => request<CostCenter>("POST", `/api/v1/company/cost-centers/${enc(id)}/activate`, { ifMatch: revision }),
    listApprovalPolicies: () => get<ApprovalPoliciesResponse>("/api/v1/company/approval-policies"),
    createApprovalPolicy: (p: ApprovalPolicyInput) => request<ApprovalPolicy>("POST", "/api/v1/company/approval-policies", { body: p }),
    updateApprovalPolicy: (id: string, p: ApprovalPolicyDetailsInput, revision: number) => request<ApprovalPolicy>("PUT", `/api/v1/company/approval-policies/${enc(id)}`, { body: p, ifMatch: revision }),
    deactivateApprovalPolicy: (id: string, revision: number) => request<ApprovalPolicy>("POST", `/api/v1/company/approval-policies/${enc(id)}/deactivate`, { ifMatch: revision }),
    activateApprovalPolicy: (id: string, revision: number) => request<ApprovalPolicy>("POST", `/api/v1/company/approval-policies/${enc(id)}/activate`, { ifMatch: revision }),
    // Journal (Phase 3.2B). Every workflow step sends the entry revision as If-Match; posting, approval and period rules are enforced by the server and PostgreSQL.
    listJournal: (q: { status?: JournalStatus; from?: string; to?: string; branchId?: string; text?: string; offset?: number } = {}) => {
      const p = new URLSearchParams();
      if (q.status) p.set("status", q.status);
      if (q.from) p.set("from", q.from);
      if (q.to) p.set("to", q.to);
      if (q.branchId) p.set("branchId", q.branchId);
      if (q.text) p.set("q", q.text);
      if (q.offset) p.set("offset", String(q.offset));
      const s = p.toString();
      return get<JournalListResponse>(`/api/v1/company/journal${s ? `?${s}` : ""}`);
    },
    getJournalEntry: (id: string) => get<JournalEntry>(`/api/v1/company/journal/${enc(id)}`),
    createJournalEntry: (entry: JournalInput) => request<JournalEntry>("POST", "/api/v1/company/journal", { body: entry }),
    updateJournalEntry: (id: string, entry: JournalInput, revision: number) => request<JournalEntry>("PUT", `/api/v1/company/journal/${enc(id)}`, { body: entry, ifMatch: revision }),
    submitJournalEntry: (id: string, revision: number) => request<JournalEntry>("POST", `/api/v1/company/journal/${enc(id)}/submit`, { ifMatch: revision }),
    withdrawJournalEntry: (id: string, revision: number) => request<JournalEntry>("POST", `/api/v1/company/journal/${enc(id)}/withdraw`, { ifMatch: revision }),
    cancelJournalEntry: (id: string, revision: number) => request<JournalEntry>("POST", `/api/v1/company/journal/${enc(id)}/cancel`, { ifMatch: revision }),
    decideJournalEntry: (id: string, approve: boolean, comment: string | null, revision: number) =>
      request<JournalEntry>("POST", `/api/v1/company/journal/${enc(id)}/decision`, { body: { approve, comment }, ifMatch: revision }),
    postJournalEntry: (id: string, revision: number) => request<JournalEntry>("POST", `/api/v1/company/journal/${enc(id)}/post`, { ifMatch: revision }),
    reverseJournalEntry: (id: string, entryDate: string, description: string | null, revision: number) =>
      request<JournalEntry>("POST", `/api/v1/company/journal/${enc(id)}/reverse`, { body: { entryDate, description }, ifMatch: revision }),
    getLedgerSettings: () => get<LedgerSettings>("/api/v1/company/ledger-settings"),
    putLedgerSettings: (roundingAccountId: string | null, revision: number) => request<LedgerSettings>("PUT", "/api/v1/company/ledger-settings", { body: { roundingAccountId }, ifMatch: revision }),
    // Reports (read-only, computed from posted entries). The CSV download needs the separate export permission.
    getTrialBalance: (q: ReportQuery & { includeZero?: boolean }) => get<TrialBalanceResponse>(`/api/v1/company/reports/trial-balance?${reportQuery(q)}${q.includeZero ? "&includeZero=true" : ""}`),
    getGeneralLedger: (accountId: string, q: ReportQuery, offset = 0) => get<GeneralLedgerResponse>(`/api/v1/company/reports/general-ledger?accountId=${enc(accountId)}&${reportQuery(q)}&limit=200&offset=${offset}`),
    downloadTrialBalanceCsv: (q: ReportQuery & { includeZero?: boolean }) => download(`/api/v1/company/reports/trial-balance?${reportQuery(q)}${q.includeZero ? "&includeZero=true" : ""}&format=csv`),
    downloadGeneralLedgerCsv: (accountId: string, q: ReportQuery) => download(`/api/v1/company/reports/general-ledger?accountId=${enc(accountId)}&${reportQuery(q)}&format=csv`),
  };
}

export type ApiClient = ReturnType<typeof createApiClient>;
