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
/** One ordered stage of a policy: stages complete strictly in order; `roleId` (optional) restricts the stage to holders of that role. */
export interface ApprovalStage { stage: number; required: number; roleId: string | null }
export type PolicyJournalType = "normal" | "reversal" | "adjusting";
export interface ApprovalPolicy {
  id: string; docType: string; source: PolicySource; branchId: string | null; minAmount: string; requiredApprovals: number;
  nameAr: string | null; nameEn: string | null; isActive: boolean; revision: number; createdAtUtc: string; updatedAtUtc: string;
  /** Risk conditions (null / "any" = do not care). A staged policy's requiredApprovals is the total over its stages. */
  journalType?: PolicyJournalType | "any" | null; rateOverride?: boolean | null; softClosed?: boolean | null; isStaged?: boolean; stages?: ApprovalStage[];
  /** Year-end condition: entries of the year-end closing ("closing") or of the opening balances ("opening"), reversals included. */
  closingKind?: "closing" | "opening" | "any" | null;
}
export interface ApprovalPoliciesResponse { policies: ApprovalPolicy[]; documentTypes: string[]; can: { create: boolean; update: boolean; activate: boolean } }
export interface ApprovalPolicyInput {
  docType: string; source: PolicySource; branchId?: string | null; minAmount: string | number; requiredApprovals?: number; nameAr: string | null; nameEn: string | null;
  journalType?: PolicyJournalType | null; rateOverride?: boolean | null; softClosed?: boolean | null; stages?: { required: number; roleId?: string | null }[] | null;
  closingKind?: "closing" | "opening" | null;
}
export type ApprovalPolicyDetailsInput = Omit<ApprovalPolicyInput, "docType">;


/** Generic import framework (Phase 3.2D). Staged values are always text; the server owns every rule. */
export type ImportStatus = "uploaded" | "staged" | "mapped" | "validated" | "submitted" | "approved" | "applying" | "applied" | "failed" | "rejected" | "cancelled" | "expired";
export interface ImportSheet { index: number; name: string; rows: number; columns: number; hidden: boolean }
export interface ImportPlanSummary { total: number; valid: number; exact: number; invalid: number; conflict: number; resolved: number; operations: number; blocking: number }
export interface ImportPlan { v: number; adapter: string; adapterVersion: number; rowsHash: string; mappingHash: string; baseline: string; summary: ImportPlanSummary; reconciliation: { key: string; value: string }[]; canApply: boolean; operationsHash: string }
export interface ImportMapping { fields: Record<string, { column: number | null; default: string | null }>; options?: { decimalSeparator?: string; thousandsSeparator?: string; dateFormat?: string } }
// ---- Reusable import mapping templates (Phase 3.9C): by header name, per company and adapter, versioned.
export interface ImportTemplate { id: string; adapter: string; name: string; version: number; isRetired: boolean; content: { fields: Record<string, { header?: string; default?: string }>; options?: Record<string, string> }; createdAtUtc: string }
export interface ImportTemplateApplication { name: string; version: number; mapping: ImportMapping; unmatched: string[] }

export interface ImportBatch {
  id: string; adapter: string; adapterVersion: number; status: ImportStatus; fileName: string; fileKind: "csv" | "xlsx"; fileSize: number; fileSha256: string; keyId: string;
  options: { headers?: string[]; sheet?: number; sheetName?: string; headerRow?: number; encoding?: string; delimiter?: string | null };
  sheets: ImportSheet[]; rowCount: number; rowsHash: string | null; planHash: string | null; revision: number; createdBy: string; createdAtUtc: string; updatedAtUtc: string;
  submittedBy: string | null; submittedAtUtc: string | null; expiresAtUtc: string; terminalAtUtc: string | null; originalAvailable: boolean; rowsAvailable: boolean; purgeAfterUtc: string | null;
  mapping: ImportMapping | null; plan: ImportPlan | null; result: Record<string, unknown> | null;
}
export interface ImportAdapterInfo { code: string; version: number; nameAr: string; nameEn: string; canUpload: boolean }
export interface ImportAdaptersResponse { adapters: ImportAdapterInfo[]; limits: { maxFileBytes: number; maxRows: number; maxColumns: number; formats: string[]; encodings: string[] } }
export interface ImportFieldDef { key: string; nameAr: string; nameEn: string; type: "text" | "code" | "integer" | "decimal" | "date" | "boolean" | "enum"; required: boolean; maxLength: number; isKey: boolean; values?: { value: string; aliases: string[] }[] | null }
export interface ImportFieldsResponse { fields: ImportFieldDef[]; resolutions: string[]; options: { decimalSeparators: string[]; thousandsSeparators: string[]; dateFormats: string[] } }
export interface ImportRowItem {
  rowNo: number; cells: (string | null)[]; status: number; normalized: Record<string, string | null> | null; issues: { field: string; code: string; detail: string | null }[] | null;
  resolution: string | null; operation: Record<string, unknown> | null; explanation: string | null; allowedResolutions: string[] | null;
}
export interface ImportRowsPage { headers: string[]; total: number; offset: number; rows: ImportRowItem[] }
export interface ImportEvent { id: number; kind: string; revision: number; actorUserId: string | null; atUtc: string; detail: Record<string, unknown> }
export interface ImportDecision { approver: string; approved: boolean; revision: number; atUtc: string; comment: string | null }
export interface ImportSettings { retentionDays: number; pendingExpiryDays: number; revision: number; configured: boolean }
export interface ImportSettingsResponse { settings: ImportSettings; can: { configure: boolean } }
export interface ImportStageOptions { sheet?: number; headerRow?: number; encoding?: string; delimiter?: string }
export interface ImportResolutionInput { rowNo: number; action: string | null }
/** Row status codes persisted by the server. */
export const ImportRowStatus = { staged: 1, valid: 2, invalid: 3, conflict: 4, resolved: 5, applied: 6, exact: 7 } as const;

/** Year-end closing and opening balances (Phase 3.2C S3). */
export interface ClosingSettings { equityAccountId: string | null; equityCode: string | null; equityNameAr: string | null; equityNameEn: string | null; requireApproval: boolean; revision: number; configured: boolean }
export interface ClosingSettingsResponse { settings: ClosingSettings; can: { configure: boolean; create: boolean; reverse: boolean } }
/** A validation of the closing. A check of a module that does not exist is "not_applicable", never "passed". */
export interface ClosingCheck { code: string; status: "passed" | "blocked" | "not_applicable"; detail: string | null; phase: "close" | "carry" | "both" }
export interface YearEndLine { accountId: string; branchId: string; debit: string; credit: string; costCenterId?: string | null }
export interface ClosingPreview {
  yearId: string; yearName: string; yearStart: string; yearEnd: string; baseCurrency: string; equityAccountId: string | null; netResult: string; isProfit: boolean;
  closingLines: YearEndLine[]; openingLines: YearEndLine[]; nextYearId: string | null; nextYearStart: string | null; checks: ClosingCheck[];
  canClose: boolean; canCarryForward: boolean; nextVersion: number; evidenceHash: string; activeClosingId: string | null;
}
export type ClosingState = "prepared" | "closed" | "opening_prepared" | "carried_forward" | "reversed" | "cancelled";
export interface FiscalYearClosing {
  id: string; yearId: string; version: number; netResult: string; equityAccountId: string; closingEntryId: string; closingStatus: string; closingNumber: string | null;
  carryforwardId: string | null; openingEntryId: string | null; openingStatus: string | null; openingNumber: string | null; toYearId: string | null;
  state: ClosingState; stale: boolean; evidenceHash: string; createdAtUtc: string; createdBy: string;
}
export interface FiscalYearCarryforward { id: string; fromYearId: string; closingId: string | null; toYearId: string; entryId: string; entryStatus: string; entryNumber: string | null; stale: boolean; evidenceHash: string; createdAtUtc: string; createdBy: string }
export interface YearEndStatus { yearId: string; name: string; startDate: string; endDate: string; closings: FiscalYearClosing[]; carryforwards: FiscalYearCarryforward[] }

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
export interface JournalApprovalDecision { id: number; documentRevision: number; approver: string; approved: boolean; decidedAtUtc: string; comment: string | null; stage?: number }
/** Progress of one stage of the effective approval plan of the current revision. */
export interface JournalPlanStage { stage: number; required: number; approved: number; roleId: string | null }
export interface JournalEventInfo { id: number; kind: string; actor: string; atUtc: string; revision: number; detail: string | null }
export interface JournalEntry {
  entry: JournalHeader; balanced: boolean; baseDebitTotal: string; baseCreditTotal: string; requiredApprovals: number;
  lines: JournalLine[]; approvals: JournalApprovalDecision[]; events: JournalEventInfo[]; plan?: JournalPlanStage[];
}
export interface JournalListResponse {
  entries: JournalHeader[];
  can: { create: boolean; approve: boolean; post: boolean; reverse: boolean; rateOverride: boolean; postInSoftClosed: boolean };
}
/** Exactly one of amount / percent per allocation, and the same mode for every allocation of a line (percentages must total exactly 100; the server converts them deterministically). */
export interface JournalAllocationInput { costCenterId: string; amount?: string | number | null; percent?: string | number | null }
/** A cost center the caller may allocate to (an active leaf inside their grants): what the journal editor offers. */
export interface AllocatableCostCenter { id: string; code: string; nameAr: string | null; nameEn: string | null; parentId: string | null; branchId: string | null }

export interface AccountCostCenterRule { accountId: string; code: string; nameAr: string | null; nameEn: string | null; required: boolean; revision: number }
export interface AccountCostCenterRulesResponse { accounts: AccountCostCenterRule[]; can: { configure: boolean } }
// ---- Tax configuration (Phase 3.10). Treatment: 1 standard, 2 zero rated, 3 exempt, 4 not applicable. Class kind: 1 item, 2 party. Rounding mode: 1 half away from zero, 2 half even; level: 1 per line, 2 per document.
export interface TaxRate { effectiveFrom: string; treatment: number; ratePercent: number }
export interface Tax { id: string; code: string; nameAr: string; nameEn: string | null; isActive: boolean; revision: number; updatedAtUtc: string; rates: TaxRate[] }
export interface TaxesResponse { taxes: Tax[]; can: { configure: boolean } }
export interface TaxInput { code?: string; nameAr: string; nameEn?: string | null; isActive?: boolean }
export interface TaxRateInput { effectiveFrom: string; treatment: number; ratePercent: number }
export interface TaxClass { id: string; kind: number; code: string; nameAr: string; nameEn: string | null; isActive: boolean; revision: number }
export interface TaxClassesResponse { classes: TaxClass[]; can: { configure: boolean } }
export interface TaxClassInput { kind: number; code?: string; nameAr: string; nameEn?: string | null; isActive?: boolean }
export interface TaxRule {
  id: string; documentType: string; operation: string; itemClassId: string | null; partyClassId: string | null; taxId: string; priority: number; includesTaxIds: string[];
  validFrom: string; validTo: string | null; isActive: boolean; treatmentOverride: number | null; revision: number;
}
export interface TaxRulesResponse { rules: TaxRule[]; can: { configure: boolean } }
export interface TaxRuleInput {
  documentType: string; operation: string; itemClassId?: string | null; partyClassId?: string | null; taxId: string; priority: number; includesTaxIds: string[];
  validFrom: string; validTo?: string | null; isActive: boolean; treatmentOverride?: number | null;
}
// ---- Attachments (Phase 3.12): files kept on the server, reached only through the authorized endpoints.
export type AttachmentSubjectKind = "local_party" | "inventory_item";
export interface Attachment { id: string; subjectKind: string; subjectId: string; fileName: string; contentType: string; sizeBytes: number; sha256: string | null; isSensitive: boolean; revision: number; createdAtUtc: string }
export interface AttachmentsResponse { items: Attachment[]; can: { upload: boolean; delete: boolean; sensitiveUpload: boolean } }
export interface AttachmentCapabilities { view: boolean; upload: boolean; delete: boolean; sensitiveView: boolean; sensitiveUpload: boolean; allowedContentTypes: string[] }

// ---- Customers and suppliers (Phase 3.9B). Role: 1 customer, 2 supplier. Payment mode: 1 cash, 2 credit, 3 mixed.
export interface PartyRole {
  id: string; partyId: string; partyCode: string; partyName: string; role: number; isActive: boolean; paymentMode: number; termDays: number; creditLimit: number | null;
  controlAccountId: string | null; controlAccountCode: string | null; representativeId: string | null; revision: number;
}
export interface PartyRolesResponse { items: PartyRole[]; can: { configure: boolean }; /** present only when the request used search, limit or offset */ total?: number }
export interface PartyRoleQuery { search?: string; limit?: number; offset?: number }
export interface PartyRoleInput { partyId?: string; role?: number; paymentMode: number; termDays?: number | null; creditLimit?: number | null; controlAccountId?: string | null; representativeId?: string | null; isActive?: boolean }
export interface SalesRepresentative { id: string; code: string; nameAr: string; nameEn: string | null; userId: string | null; isActive: boolean; revision: number }
export interface SalesRepresentativesResponse { items: SalesRepresentative[]; can: { configure: boolean } }
export interface SalesRepresentativeInput { code?: string; nameAr: string; nameEn?: string | null; userId?: string | null; isActive?: boolean }

export interface TaxAssignment { subjectId: string; classId: string }
export interface TaxAssignmentsResponse { assignments: TaxAssignment[]; can: { configure: boolean } }
export interface TaxPolicy { configured: boolean; id?: string; version?: number; moneyDecimals?: number; mode?: number; level?: number; createdAtUtc?: string }
export interface TaxPolicyResponse { policy: TaxPolicy; can: { configure: boolean } }
export interface TaxPolicyInput { moneyDecimals: number; mode: number; level: number }
export interface TaxResolutionComponent { taxId: string; code: string; treatment: number; ratePercent: number; priority: number; includesInBase: string[]; ruleRef: string | null }
export interface TaxResolution { policyVersion: number; policy: { moneyDecimals: number; mode: number; level: number }; components: TaxResolutionComponent[] }

// ---- Pricing configuration (Phase 3.11). margin action: 1 warn, 2 require approval, 3 block. list precedence: 1 scope then priority (default), 2 priority then scope. discount precedence: 1 specificity then priority (default), 2 priority then specificity.
export interface PricingPolicy { version: number; isDefault: boolean; priceDecimals: number; moneyDecimals: number; minMarginPercent: number | null; marginAction: number; listPrecedence: number; discountPrecedence: number; createdAtUtc: string | null }
export interface PricingPolicyResponse { policy: PricingPolicy; can: { configure: boolean } }
export interface PricingPolicyInput { priceDecimals: number; moneyDecimals: number; minMarginPercent: number | null; marginAction: number; listPrecedence: number; discountPrecedence: number }
export interface PriceList { id: string; code: string; nameAr: string; nameEn: string | null; priority: number; currency: string; validFrom: string; validTo: string | null; isActive: boolean; customerId: string | null; customerGroupId?: string | null; revision: number; updatedAtUtc: string }
export interface PriceListsResponse { priceLists: PriceList[]; can: { configure: boolean } }
export interface PriceListInput { code?: string; nameAr: string; nameEn?: string | null; priority: number; currency?: string; validFrom: string; validTo?: string | null; isActive?: boolean; customerId?: string | null; customerGroupId?: string | null }
export interface PriceEntry { id: string; priceListId: string; itemId: string | null; itemGroupId: string | null; unitId: string; minQuantity: number; unitPrice: number; isActive: boolean; revision: number; updatedAtUtc: string }
export interface PriceEntriesResponse { entries: PriceEntry[]; can: { configure: boolean } }
export interface PriceEntryInput { itemId?: string | null; itemGroupId?: string | null; unitId?: string; minQuantity?: number; unitPrice: number; isActive?: boolean }
export interface DiscountRule { id: string; code: string; nameAr: string; nameEn: string | null; priority: number; validFrom: string; validTo: string | null; isActive: boolean; percent: number; minQuantity: number; customerId: string | null; customerGroupId?: string | null; itemId: string | null; itemGroupId: string | null; revision: number; updatedAtUtc: string }
export interface DiscountRulesResponse { discounts: DiscountRule[]; can: { configure: boolean } }
export interface DiscountRuleInput { code?: string; nameAr: string; nameEn?: string | null; priority: number; validFrom: string; validTo?: string | null; isActive?: boolean; percent: number; minQuantity?: number; customerId?: string | null; customerGroupId?: string | null; itemId?: string | null; itemGroupId?: string | null }
export interface PricingQuoteRequest { date: string; currency: string; itemId: string; itemGroupId?: string | null; unitId: string; quantity: number; customerId?: string | null; unitCost?: number | null; override?: { unitPrice?: number | null; discountPercent?: number | null; reason: string } | null }
// Customer groups (company migration 0027). A customer belongs to at most one group; the assignment revision 0 means never assigned.
export interface CustomerGroup { id: string; code: string; nameAr: string; nameEn: string | null; isActive: boolean; revision: number; updatedAtUtc: string }
export interface CustomerGroupsResponse { customerGroups: CustomerGroup[]; can: { configure: boolean } }
export interface CustomerGroupInput { code?: string; nameAr: string; nameEn?: string | null; isActive?: boolean }
export interface CustomerGroupAssignment { partyId: string; customerGroupId: string | null; customerGroupCode: string | null; revision: number }
export interface CustomerGroupAssignmentsResponse { assignments: CustomerGroupAssignment[]; can: { configure: boolean } }
export interface PricingTraceStep { kind: string; ruleRef: string; outcome: string }
export interface PricingQuote {
  version: string; contentHash: string;
  policy: { priceDecimals: number; moneyDecimals: number; minMarginPercent: number | null; marginAction: number; listPrecedence: number; discountPrecedence: number };
  result: { listUnitPrice: number; priceRuleRef: string; discountPercent: number; discountRuleRef: string | null; netUnitPrice: number; lineNet: number; overridden: boolean; overrideReason: string | null; marginPercent: number | null; margin: number; requiresApproval: boolean; trace: PricingTraceStep[] };
}

export interface CostCenterBranchRule { costCenterId: string; restricted: boolean; branchIds: string[]; revision: number }
export interface CostCenterBranchRulesResponse { costCenters: CostCenterBranchRule[]; can: { configure: boolean } }
export interface JournalLineInput {
  accountId: string; description?: string | null; currency?: string | null; debit?: string | number | null; credit?: string | number | null; branchId?: string | null;
  rateOverride?: string | number | null; rateOverrideReason?: string | null; allocations?: JournalAllocationInput[] | null;
}
export interface JournalInput { entryDate: string; description?: string | null; branchId: string; lines: JournalLineInput[]; adjustsId?: string | null }
export interface LedgerSettings { roundingAccountId: string | null; revision: number; configured: boolean }

export interface ReportFigures { opening: string; debit: string; credit: string; closing: string }
export interface TrialBalanceRowInfo { accountId: string; code: string; nameAr: string | null; nameEn: string | null; parentId: string | null; level: number; isGroup: boolean; opening: string; debit: string; credit: string; closing: string; comparative?: ReportFigures | null; change?: string | null }
export interface ResolvedRangeInfo { from: string; to: string; fiscalYearId: string | null; fiscalPeriodId: string | null; label: string }
export interface ReportPeriodInfo { current: ResolvedRangeInfo; comparative: ResolvedRangeInfo | null; comparativeUnavailable: string | null }
export interface TrialBalanceResponse {
  from: string; to: string; baseCurrency: string; balanced: boolean; sort?: string; maxLevel?: number | null; period?: ReportPeriodInfo; totals: { opening: string; debit: string; credit: string; closing: string };
  comparativeTotals?: ReportFigures | null; rows: TrialBalanceRowInfo[];
}
export interface ReportColumnInfo { key: string; titleAr: string; titleEn: string; type: string; default: boolean }
export interface ReportParameterInfo { name: string; kind: string; required: boolean; titleAr: string | null; titleEn: string | null }
export interface ReportDefinitionInfo { id: string; titleAr: string; titleEn: string; canExport: boolean; formats: string[]; supportsComparison: boolean; supportsGrouping: boolean; parameters: ReportParameterInfo[]; columns: ReportColumnInfo[] }
export type ReconciliationStatusInfo = "passed" | "failed" | "not_applicable";
export interface ReconciliationCheckInfo { code: string; status: ReconciliationStatusInfo; differences: number; samples: { accountId: string; code: string; item: string; expected: string; actual: string }[] }
export interface ReconciliationResponse { from: string; to: string; baseCurrency: string; allPassed: boolean; checks: ReconciliationCheckInfo[] }
export interface GeneralLedgerLineInfo {
  entryId: string; number: string; entryDate: string; entryType: string; entryDescription: string | null; lineDescription: string | null; accountId: string; currency: string;
  debit: string; credit: string; rate: string; rateId: number | null; baseDebit: string; baseCredit: string; branchId: string; isRounding: boolean; runningBalance: string;
}
export interface GeneralLedgerResponse {
  from: string; to: string; baseCurrency: string; accountId: string; includesDescendants: boolean; opening: string; totalDebit: string; totalCredit: string; closing: string;
  totalLines: number; offset: number; lines: GeneralLedgerLineInfo[];
}
/** What the caller selected: a fiscal period, a fiscal year, or a date range (the server accepts exactly one), plus the optional comparison and presentation options. */
export type ReportComparison = "none" | "previous_period" | "previous_year";
export interface ReportQuery {
  from?: string; to?: string; fiscalYearId?: string; fiscalPeriodId?: string; branchId?: string; costCenterId?: string; excludeClosing?: boolean;
  comparison?: ReportComparison; maxLevel?: number; sort?: string; columns?: string[]; grouping?: "none" | "month"; /** The language of an exported file (titles and, for spreadsheets, the reading direction). */ lang?: "ar" | "en";
}
export type ReportFormat = "csv" | "xlsx" | "pdf";

/** Financial statements and their classification (Phase 3.3 S3). Amounts are exact decimal text in the base currency, in the natural presentation sign of each section. */
export type StatementKind = "profit-and-loss" | "balance-sheet" | "trading-account";
export interface StatementLineInfo { kind: "header" | "account" | "subtotal" | "total" | "note"; key: string; accountId: string | null; code: string | null; nameAr: string | null; nameEn: string | null; level: number; current: string; comparative: string | null; change: string | null }
// ---- Inventory master data (Phase 3.4): units, item groups, items, barcodes, settings. No stock, quantities or costs exist in this API.
/** 1 FIFO, 2 moving weighted average (D-017). */
export type ValuationMethodCode = 1 | 2;
export interface InventoryUnit { id: string; code: string; nameAr: string | null; nameEn: string | null; decimals: number; isActive: boolean; revision: number; createdAtUtc: string; updatedAtUtc: string }
export interface InventoryUnitInput { code?: string; nameAr: string | null; nameEn: string | null; decimals: number }
export interface InventoryGroup {
  id: string; code: string; nameAr: string | null; nameEn: string | null; parentId: string | null;
  /** The group's own setting; null = inherit. */
  valuation: ValuationMethodCode | null;
  /** The resolved method (own, else the nearest ancestor's, else the company default); null while nobody decided. */
  effectiveValuation: ValuationMethodCode | null;
  isActive: boolean; revision: number; createdAtUtc: string; updatedAtUtc: string;
}
export interface InventoryGroupInput { code?: string; nameAr: string | null; nameEn: string | null; parentId: string | null; valuation: ValuationMethodCode | null }
export interface InventoryItemUnit { unitId: string; unitCode: string; factor: number }
/** 1 goods, 2 service. */
export type ItemTypeCode = 1 | 2;
export interface InventoryItem {
  id: string; code: string; nameAr: string | null; nameEn: string | null; itemType: ItemTypeCode; groupId: string; baseUnitId: string; purchaseUnitId: string | null; salesUnitId: string | null;
  isLot: boolean; isSerial: boolean; hasExpiry: boolean; notes: string | null; isActive: boolean; revision: number; effectiveValuation: ValuationMethodCode | null;
  alternateUnits: InventoryItemUnit[]; createdAtUtc: string; updatedAtUtc: string;
}
export interface InventoryItemInput {
  /** Omit (or blank) on creation to get an automatic code; it can never change afterwards. */
  code?: string | null; nameAr: string | null; nameEn: string | null; itemType: ItemTypeCode; groupId: string; baseUnitId: string; purchaseUnitId: string | null; salesUnitId: string | null;
  isLot: boolean; isSerial: boolean; hasExpiry: boolean; notes: string | null; alternateUnits: { unitId: string; factor: number }[];
}
export interface InventoryItemQuery { search?: string; groupId?: string; active?: boolean; type?: ItemTypeCode; offset?: number; limit?: number }
export interface InventoryItemPage { items: InventoryItem[]; total: number }
/** 1 EAN-13, 2 EAN-8, 3 UPC-A, 4 internal. */
export interface InventoryBarcode { id: string; itemId: string; unitId: string; barcode: string; kind: 1 | 2 | 3 | 4; isActive: boolean; createdAtUtc: string; updatedAtUtc: string }
export interface InventoryBarcodeLookup { barcode: InventoryBarcode; itemCode: string; itemNameAr: string | null; itemNameEn: string | null; unitCode: string; unitFactor: number }
export interface InventorySettings { defaultValuation: ValuationMethodCode | null; revision: number }
export interface InventoryCapabilities {
  unitsView: boolean; unitsCreate: boolean; unitsUpdate: boolean; unitsActivate: boolean; groupsView: boolean; groupsCreate: boolean; groupsUpdate: boolean; groupsActivate: boolean;
  itemsView: boolean; itemsCreate: boolean; itemsUpdate: boolean; itemsActivate: boolean; barcodesCreate: boolean; barcodesActivate: boolean; settingsView: boolean; settingsConfigure: boolean;
}

// ---- Stock movement documents (Phase 3.5): receipt, issue, transfer up to approval. No stock quantity, movement ledger, cost or accounting exists in this API.
/** 1 receipt, 2 issue, 3 transfer. */
export type StockDocType = 1 | 2 | 3;
/** 1 draft, 2 submitted, 3 approved, 4 rejected, 5 cancelled. */
export type StockDocStatus = 1 | 2 | 3 | 4 | 5;
export interface StockDocumentLine {
  lineNo: number; itemId: string; itemCode: string; itemNameAr: string | null; itemNameEn: string | null; unitId: string; unitCode: string; quantity: number; factor: number; baseQuantity: number; baseUnitCode: string; notes: string | null;
}
/** kind: 1 created, 2 updated, 3 submitted, 4 withdrawn, 5 approved, 6 rejected, 7 cancelled. */
export interface StockDocumentEvent { id: number; kind: number; fromStatus: number | null; toStatus: number; actorUserId: string; atUtc: string; note: string | null }
export interface StockDocument {
  id: string; docType: StockDocType; status: StockDocStatus; branchId: string; sourceWarehouseId: string | null; targetWarehouseId: string | null; documentDate: string; reference: string | null; notes: string | null;
  number: string | null; fiscalYearId: string | null; createdBy: string; createdAtUtc: string; submittedBy: string | null; submittedAtUtc: string | null; decidedBy: string | null; decidedAtUtc: string | null; decisionNote: string | null;
  cancelledBy: string | null; cancelledAtUtc: string | null; cancelReason: string | null; revision: number; updatedAtUtc: string; lines: StockDocumentLine[]; events: StockDocumentEvent[];
}
export interface StockDocumentSummary {
  id: string; docType: StockDocType; status: StockDocStatus; number: string | null; documentDate: string; branchId: string; sourceWarehouseId: string | null; targetWarehouseId: string | null; reference: string | null;
  lineCount: number; revision: number; createdAtUtc: string; updatedAtUtc: string;
}
export interface StockDocumentLineInput { itemId: string; unitId: string; quantity: number; notes?: string | null }
export interface StockDocumentInput { docType?: StockDocType; sourceWarehouseId: string | null; targetWarehouseId: string | null; documentDate: string; reference: string | null; notes: string | null; lines: StockDocumentLineInput[] }
export interface StockDocumentQuery { type?: StockDocType; status?: StockDocStatus; warehouseId?: string; from?: string; to?: string; search?: string; offset?: number; limit?: number }
/** For each action, the ids of the warehouses on which the caller holds the permission. */
export interface StockDocumentCapabilities { view: string[]; create: string[]; update: string[]; submit: string[]; approve: string[]; cancel: string[]; post: string[]; reverse: string[] }

/** Execution of an approved stock document (Phase 3.7B/3.7C). accountingState: 1 pending (draft journal awaiting approval/posting), 2 no journal required, 3 journal posted, 4 failed (journal cancelled outside the reversal), 5 reversed. */
export interface StockPostingLine { lineNo: number; itemId: string; warehouseId: string; baseQuantity: number; amount: number }
export interface StockReversal { id: string; costOperationId: string; journalAction: number; journalEntryId: string | null; effectiveDate: string; reversedAtUtc: string; reversedBy: string; reason: string }
export interface StockPosting {
  id: string; documentId: string; docType: number; effectiveDate: string; postedAtUtc: string; postedBy: string; costOperationId: string; accountingVersionId: string | null; journalEntryId: string | null;
  totalValue: number; accountingState: number; replayed: boolean; journalStatus: number | null; reversal: StockReversal | null; lines: StockPostingLine[];
}
export interface StockPostLineInput { lineNo: number; amount: number }

/** Stock balances, item ledger and the reconciliation with the General Ledger (Phase 3.8A, read-only). Quantities are in the item's base unit; values in the company base currency. */
export interface StockBalanceRow {
  itemId: string; itemCode: string; itemNameAr: string | null; itemNameEn: string | null; groupId: string; warehouseId: string; warehouseCode: string; warehouseNameAr: string | null; warehouseNameEn: string | null;
  baseUnitCode: string; method: number; quantity: number; value: number; averageUnitCost: number;
}
export interface StockBalancePage { balances: StockBalanceRow[]; total: number; totalValue: number; asOf: string | null }
export interface StockBalanceQuery { itemId?: string; warehouseId?: string; search?: string; asOf?: string; includeZero?: boolean; offset?: number; limit?: number }
export interface StockLedgerRow {
  seq: number; effectiveDate: string; kind: number; direction: number; quantity: number; amount: number; quantityAfter: number; valueAfter: number; sourceType: string | null; sourceId: string | null; sourceNumber: string | null;
}
export interface StockLedgerPage { itemId: string; warehouseId: string; openingQuantity: number; openingValue: number; closingQuantity: number; closingValue: number; total: number; rows: StockLedgerRow[] }
export interface StockLedgerQuery { itemId: string; warehouseId: string; from?: string; to?: string; offset?: number; limit?: number }
export interface StockBalanceItem { id: string; code: string; nameAr: string | null; nameEn: string | null }
export interface StockReconciliationAccount { accountId: string; code: string; nameAr: string | null; nameEn: string | null; ledgerBalance: number }
export interface StockReconciliation { asOf: string; costingValue: number; ledgerValue: number; difference: number; accounts: StockReconciliationAccount[] }
export interface StockWarehouseRef { id: string; branchId: string; code: string; nameAr: string | null; nameEn: string | null; isActive: boolean }
export interface StockItemUnit { id: string; code: string; nameAr: string | null; nameEn: string | null; decimals: number; factor: number; isBase: boolean }
export interface StockItemRef { id: string; code: string; nameAr: string | null; nameEn: string | null; units: StockItemUnit[] }

// ---- Stocktake (Phase 3.8B-1): count sheet of one warehouse with the book snapshot and the variance, up to approval. Creates no stock movement and no accounting entry.
/** 1 draft, 2 submitted, 3 approved, 4 rejected, 5 cancelled. */
export type StocktakeStatus = 1 | 2 | 3 | 4 | 5;
/** Quantities: counted in the line's unit; countedBaseQuantity, bookQuantity (snapshot), currentBookQuantity and variance (counted base - book) in the item's base unit. stale = the current book differs from the snapshot. */
export interface StocktakeLine {
  lineNo: number; itemId: string; itemCode: string; itemNameAr: string | null; itemNameEn: string | null; unitId: string; unitCode: string; countedQuantity: number | null; factor: number;
  countedBaseQuantity: number | null; bookQuantity: number; variance: number | null; baseUnitCode: string; reason: string | null; currentBookQuantity: number; stale: boolean;
  /** The units the line may be counted in (base first, active alternates and the saved unit) with their exact factors (Phase 3.8B-2, follow-up M2). */
  units?: StockItemUnit[];
}
/** kind: 1 created, 2 updated, 3 submitted, 4 withdrawn, 5 approved, 6 rejected, 7 cancelled, 8 sheet generated. */
export interface Stocktake {
  id: string; status: StocktakeStatus; branchId: string; warehouseId: string; countDate: string; reference: string | null; notes: string | null; number: string | null; fiscalYearId: string | null;
  snapshotAtUtc: string; stale: boolean; createdBy: string; createdAtUtc: string; submittedBy: string | null; submittedAtUtc: string | null; decidedBy: string | null; decidedAtUtc: string | null; decisionNote: string | null;
  cancelledBy: string | null; cancelledAtUtc: string | null; cancelReason: string | null; revision: number; updatedAtUtc: string; lines: StocktakeLine[]; events: StockDocumentEvent[];
  /** The one adjustment generated from this approved stocktake, if any (Phase 3.8B-2). */
  adjustmentId?: string | null; adjustmentNumber?: string | null;
}
export interface StocktakeSummary {
  id: string; status: StocktakeStatus; number: string | null; countDate: string; branchId: string; warehouseId: string; reference: string | null; lineCount: number; countedLines: number; varianceLines: number;
  revision: number; createdAtUtc: string; updatedAtUtc: string;
}
export interface StocktakeLineInput { itemId: string; unitId: string; countedQuantity: number | null; reason: string | null }
export interface StocktakeInput { warehouseId: string; countDate: string; reference: string | null; notes: string | null; lines: StocktakeLineInput[] }
export interface StocktakeQuery { status?: StocktakeStatus; warehouseId?: string; from?: string; to?: string; search?: string; offset?: number; limit?: number }
/** For each action, the ids of the warehouses on which the caller holds the stocktake permission. */
export interface StocktakeCapabilities { view: string[]; create: string[]; update: string[]; submit: string[]; approve: string[]; cancel: string[]; adjust?: string[]; post?: string[]; reverse?: string[] }

// ---- Party registry (Phase 3.9A): company-local party records linked to a shared identity of the central registry; central proposal and approval. No balance is ever part of a shared identity.
export type PartyKind = 1 | 2;
export type PartyLinkState = 1 | 2 | 3;
export interface SharedIdentity { globalPartyId: string; kind: PartyKind; legalNameAr: string; legalNameEn: string | null; taxNumber: string | null; registrationNumber: string | null; status: 1 | 2; revision: number }
export interface SharedIdentityInput { kind: PartyKind | null; legalNameAr: string | null; legalNameEn: string | null; taxNumber: string | null; registrationNumber: string | null; status?: 1 | 2 | null; note?: string | null; viewedSharedRevision?: number | null }
export interface PartyRequest {
  id: string; kind: 1 | 2; status: 1 | 2 | 3; outcome: 1 | 2 | 3 | null; partyKind: PartyKind; legalNameAr: string; legalNameEn: string | null; taxNumber: string | null; registrationNumber: string | null;
  proposedStatus: 1 | 2; note: string | null; attempts: number; lastAttemptAtUtc: string | null; lastErrorCode: string | null; createdBy: string; createdAtUtc: string; deliveredAtUtc: string | null; completedAtUtc: string | null;
  /** Revision of the shared identity a change was based on; the central decision note of a rejection. */
  basedOnRevision?: number | null; decisionNote?: string | null;
}
export interface LocalParty {
  id: string; code: string; displayName: string; notes: string | null; isActive: boolean; linkState: PartyLinkState; globalPartyId: string | null; revision: number; createdBy: string; createdAtUtc: string; updatedAtUtc: string;
  identity: SharedIdentity | null; requests: PartyRequest[];
  /** The registry could not be read: the shared identity is not shown. */
  registryUnavailable?: boolean;
}
export interface LocalPartySummary { id: string; code: string; displayName: string; isActive: boolean; linkState: PartyLinkState; legalNameAr: string | null; legalNameEn: string | null; hasOpenRequest: boolean; revision: number; updatedAtUtc: string }
export interface LocalPartyInput { code: string; displayName: string; notes: string | null; isActive: boolean; identity?: SharedIdentityInput }
export interface PartyCapabilities { view: boolean; create: boolean; update: boolean; submit: boolean; configure: boolean }
export interface PartySyncResult { delivered: number; applied: number; failed: number; waiting: number; registryUnavailable?: boolean }
export interface PartyReconciliationFinding { code: string; localPartyId: string | null; requestId: string | null; globalPartyId: string | null; repairable: boolean; repaired: boolean; proposalId?: string | null }
export interface PartyReconciliation { repairRequested: boolean; registryUnavailable?: boolean; findings: PartyReconciliationFinding[] }
export interface PartyProposalSummary { id: string; companyId: string; companyNameAr: string | null; companyNameEn: string | null; kind: 1 | 2; legalNameAr: string; legalNameEn: string | null; taxNumber: string | null; registrationNumber: string | null; proposedAtUtc: string; status: 1 | 2 | 3; revision: number }
export interface PartyProposal extends PartyProposalSummary {
  localPartyId: string; targetGlobalPartyId: string | null; partyKind: PartyKind; proposedStatus: 1 | 2; note: string | null; proposedBy: string; proposedByName: string | null; resolution: 1 | 2 | 3 | null;
  outcomeGlobalPartyId: string | null; decidedBy: string | null; decidedAtUtc: string | null; decisionNote: string | null; target: SharedIdentity | null; matches: SharedIdentity[];
  /** Change proposals: the identity revision the change was based on (a different current revision cannot be approved). */
  basedOnRevision?: number | null;
}

// ---- Stock adjustment (Phase 3.8B-2): generated once from an approved stocktake; posted (shortages at the engine cost, surpluses at the entered amount) and reversed like a stock document.
/** direction: -1 shortage (outflow), 1 surplus (inflow); quantity, countedQuantity and bookQuantity in the base unit; amount is the value booked once posted. */
export interface StockAdjustmentLine {
  lineNo: number; itemId: string; itemCode: string; itemNameAr: string | null; itemNameEn: string | null; unitId: string; unitCode: string; direction: -1 | 1; quantity: number; countedQuantity: number;
  bookQuantity: number; reason: string | null; amount: number | null;
}
export interface StockAdjustmentReversal { id: string; issueOperationId: string | null; receiptOperationId: string | null; journalAction: number; journalEntryId: string | null; effectiveDate: string; reversedAtUtc: string; reversedBy: string; reason: string }
/** accountingState as for stock documents: 1 pending, 2 no journal required, 3 posted, 4 failed, 5 reversed. */
export interface StockAdjustmentPosting {
  id: string; effectiveDate: string; postedAtUtc: string; postedBy: string; issueOperationId: string | null; receiptOperationId: string | null; accountingVersionId: string | null; journalEntryId: string | null;
  journalStatus: number | null; totalValue: number; accountingState: number; reversal: StockAdjustmentReversal | null;
}
export interface StockAdjustment {
  id: string; stocktakeId: string; stocktakeNumber: string | null; branchId: string; warehouseId: string; adjustmentDate: string; number: string; fiscalYearId: string; createdBy: string; createdAtUtc: string;
  replayed: boolean; lines: StockAdjustmentLine[]; posting: StockAdjustmentPosting | null;
}

// ---- Inventory accounting configuration (Phase 3.7A): which ledger accounts hold inventory and take the other side of stock movements. Versioned and immutable; nothing is posted by it.
export interface InventoryAccountingVersion { id: string; versionNo: number; createdAtUtc: string; createdBy: string; note: string | null; groupCount: number; contentHash: string }
export interface InventoryAccountingConfig {
  version: InventoryAccountingVersion | null; versionNo: number; defaultInventoryAccountId: string | null; receiptCounterAccountId: string | null; issueCounterAccountId: string | null; groups: { groupId: string; accountId: string }[];
  /** Phase 3.8B-2: the counter account of stock adjustments (null until configured). */
  adjustmentCounterAccountId?: string | null;
  /** Phase 3.8B-3: the provisional obligation account O (a liability) and the settlement variance account V (null = the issue counter account). */
  obligationAccountId?: string | null; varianceAccountId?: string | null;
}
export interface InventoryAccountingAccountRef { id: string; code: string; nameAr: string | null; nameEn: string | null; type: number }
export interface InventoryAccountingGroupRef { id: string; code: string; nameAr: string | null; nameEn: string | null; parentId: string | null }
export interface InventoryAccountingResponse { configuration: InventoryAccountingConfig; canConfigure: boolean; accounts: InventoryAccountingAccountRef[]; groups: InventoryAccountingGroupRef[] }
export interface InventoryAccountingInput { defaultInventoryAccountId: string | null; receiptCounterAccountId: string | null; issueCounterAccountId: string | null; adjustmentCounterAccountId?: string | null; obligationAccountId?: string | null; varianceAccountId?: string | null; groups: { groupId: string; accountId: string }[]; note: string | null }

// ---- Negative-stock exception (Phase 3.8B-3): off by default; an eligible moving-average item may be issued above its stock by an explicit, segregated exception approval. The uncovered part is a provisional
// obligation settled by later receipts; the cost position never goes negative. Every rule (eligibility, approval, provisional cost, settlement, reversal, close) is enforced by the server.
export interface NegativeStockSettings { versionNo: number; enabled: boolean; requireSecondApprover: boolean; note: string | null; createdBy: string | null; createdAtUtc: string | null }
export interface NegativeItemFlag { itemId: string; code: string; nameAr: string | null; nameEn: string | null; eligible: boolean; effectiveValuation: number | null; tracked: boolean; isActive: boolean; hasOpenObligation: boolean; note: string | null; changedBy: string | null; changedAtUtc: string | null }
/** source: 1 last average, 2 latest stock-document receipt, 3 manual (justified). problem: a stable refusal code for the line. */
export interface NegativeExceptionLine { lineNo: number; itemId: string; warehouseId: string; quantity: number; onHand: number; covered: number; uncovered: number; source: number | null; sourceEntryId: number | null; sourceQuantity: number | null; sourceValue: number | null; provisionalTotal: number | null; provisionalUnitCost: number | null; justification: string | null; problem: string | null }
export interface NegativeExceptionPlan { documentId: string; documentRevision: number; documentDate: string; needed: boolean; accountingVersionId: string | null; obligationAccountId: string | null; varianceAccountId: string | null; counterAccountId: string | null; problems: string[]; hash: string; lines: NegativeExceptionLine[] }
export interface NegativeApproval { id: string; approverId: string; approvedAtUtc: string; reason: string; snapshotHash: string; current: boolean }
export interface NegativeException { plan: NegativeExceptionPlan; enabled: boolean; requireSecondApprover: boolean; approvalsRequired: number; validApprovals: number; posted: boolean; approvals: NegativeApproval[] }
/** amount is sent as the decimal text typed (the server reads it exactly; no binary floating point). */
export interface NegativeManualCost { lineNo: number; amount: string; justification: string }
export interface NegativeSettlement { id: string; receiptPostingId: string; receiptDocumentId: string; receiptNumber: string | null; receiptLineNo: number; effectiveDate: string; matchedQuantity: number; provisionalAmount: number; actualAmount: number; difference: number; journalEntryId: string | null; reversesSettlementId: string | null; reversed: boolean }
/** status: 1 open, 2 settled, 3 reversed. */
export interface NegativeObligation { id: string; seq: number; itemId: string; itemCode: string; itemNameAr: string | null; itemNameEn: string | null; warehouseId: string; documentId: string; documentNumber: string | null; lineNo: number; effectiveDate: string; originalQuantity: number; provisionalTotal: number; openQuantity: number; openProvisional: number; source: number; justification: string | null; status: number; ageDays: number; journalEntryId: string; journalStatus: number | null; obligationAccountId: string; varianceAccountId: string; settlements: NegativeSettlement[] }
export interface NegativeCloseReport { periodEnd: string; open: NegativeObligation[]; expectedObligationBalance: number; ledgerObligationBalance: number; settlementDifferences: number; ledgerVariance: number; failedSettlementJournals: number; reconciled: boolean; blocksFinalClose: boolean }

export interface StatementMappingVersionInfo { id: string; versionNo: number; createdAtUtc: string; createdBy: string; source: "manual" | "template"; note: string | null; entryCount: number; contentHash: string }
export interface StatementResponse {
  kind: StatementKind; applicable: boolean; notApplicableReason: string | null; complete: boolean; asOf: boolean; baseCurrency: string; hasComparative: boolean; mapping: StatementMappingVersionInfo | null; period: ReportPeriodInfo;
  issues: { code: string; count: number; amount: string }[]; checks: { code: string; status: "passed" | "failed" | "not_applicable"; difference: string }[]; lines: StatementLineInfo[];
}
export interface StatementQuery extends ReportQuery { mappingVersion?: number }
export interface StatementSectionInfo { key: string; statement: "balance_sheet" | "profit_and_loss"; types: string[] }
export interface StatementMappingAccount { id: string; code: string; nameAr: string | null; nameEn: string | null; parentId: string | null; type: string; nature: string; isGroup: boolean; templateKey: string | null; effectiveSection: string | null }
export interface StatementMappingResponse { version: StatementMappingVersionInfo | null; sections: StatementSectionInfo[]; entries: { accountId: string; section: string }[]; accounts: StatementMappingAccount[] }
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
  async function download(path: string, accept = "text/csv"): Promise<Blob> {
    const headers: Record<string, string> = { Accept: accept };
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
  const reportQuery = (q: ReportQuery) => [
    q.fiscalPeriodId ? `fiscalPeriodId=${enc(q.fiscalPeriodId)}` : q.fiscalYearId ? `fiscalYearId=${enc(q.fiscalYearId)}` : `from=${enc(q.from ?? "")}&to=${enc(q.to ?? "")}`,
    q.branchId ? `branchId=${enc(q.branchId)}` : "", q.costCenterId ? `costCenterId=${enc(q.costCenterId)}` : "", q.excludeClosing ? "excludeClosing=true" : "",
    q.comparison && q.comparison !== "none" ? `comparison=${q.comparison}` : "", q.maxLevel !== undefined ? `maxLevel=${q.maxLevel}` : "", q.sort ? `sort=${enc(q.sort)}` : "",
    q.columns && q.columns.length > 0 ? `columns=${enc(q.columns.join(","))}` : "", q.grouping && q.grouping !== "none" ? `grouping=${q.grouping}` : "", q.lang ? `lang=${q.lang}` : "",
  ].filter(Boolean).join("&");

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
    // Inventory master data (Phase 3.4). Changes carry the revision as If-Match; the server enforces every rule and every permission.
    getInventoryCapabilities: () => get<InventoryCapabilities>("/api/v1/company/inventory/capabilities"),
    getInventorySettings: () => get<InventorySettings>("/api/v1/company/inventory/settings"),
    putInventorySettings: (defaultValuation: ValuationMethodCode | null, revision: number) => request<InventorySettings>("PUT", "/api/v1/company/inventory/settings", { body: { defaultValuation }, ifMatch: revision }),
    listInventoryUnits: () => get<{ units: InventoryUnit[] }>("/api/v1/company/inventory/units"),
    createInventoryUnit: (unit: InventoryUnitInput) => request<InventoryUnit>("POST", "/api/v1/company/inventory/units", { body: unit }),
    updateInventoryUnit: (id: string, unit: InventoryUnitInput, revision: number) => request<InventoryUnit>("PUT", `/api/v1/company/inventory/units/${enc(id)}`, { body: unit, ifMatch: revision }),
    deactivateInventoryUnit: (id: string, revision: number) => request<InventoryUnit>("POST", `/api/v1/company/inventory/units/${enc(id)}/deactivate`, { ifMatch: revision }),
    activateInventoryUnit: (id: string, revision: number) => request<InventoryUnit>("POST", `/api/v1/company/inventory/units/${enc(id)}/activate`, { ifMatch: revision }),
    listInventoryGroups: () => get<{ groups: InventoryGroup[] }>("/api/v1/company/inventory/groups"),
    createInventoryGroup: (group: InventoryGroupInput) => request<InventoryGroup>("POST", "/api/v1/company/inventory/groups", { body: group }),
    updateInventoryGroup: (id: string, group: InventoryGroupInput, revision: number) => request<InventoryGroup>("PUT", `/api/v1/company/inventory/groups/${enc(id)}`, { body: group, ifMatch: revision }),
    deactivateInventoryGroup: (id: string, revision: number) => request<InventoryGroup>("POST", `/api/v1/company/inventory/groups/${enc(id)}/deactivate`, { ifMatch: revision }),
    activateInventoryGroup: (id: string, revision: number) => request<InventoryGroup>("POST", `/api/v1/company/inventory/groups/${enc(id)}/activate`, { ifMatch: revision }),
    listInventoryItems: (q: InventoryItemQuery = {}) => {
      const p = new URLSearchParams();
      if (q.search) p.set("search", q.search);
      if (q.groupId) p.set("groupId", q.groupId);
      if (q.active !== undefined) p.set("active", String(q.active));
      if (q.type !== undefined) p.set("type", String(q.type));
      if (q.offset) p.set("offset", String(q.offset));
      if (q.limit) p.set("limit", String(q.limit));
      const qs = p.toString();
      return get<InventoryItemPage>(`/api/v1/company/inventory/items${qs ? "?" + qs : ""}`);
    },
    getInventoryItem: (id: string) => get<InventoryItem>(`/api/v1/company/inventory/items/${enc(id)}`),
    createInventoryItem: (item: InventoryItemInput) => request<InventoryItem>("POST", "/api/v1/company/inventory/items", { body: item }),
    updateInventoryItem: (id: string, item: InventoryItemInput, revision: number) => request<InventoryItem>("PUT", `/api/v1/company/inventory/items/${enc(id)}`, { body: item, ifMatch: revision }),
    deactivateInventoryItem: (id: string, revision: number) => request<InventoryItem>("POST", `/api/v1/company/inventory/items/${enc(id)}/deactivate`, { ifMatch: revision }),
    activateInventoryItem: (id: string, revision: number) => request<InventoryItem>("POST", `/api/v1/company/inventory/items/${enc(id)}/activate`, { ifMatch: revision }),
    listItemBarcodes: (itemId: string) => get<{ barcodes: InventoryBarcode[] }>(`/api/v1/company/inventory/items/${enc(itemId)}/barcodes`),
    addItemBarcode: (itemId: string, barcode: { barcode: string; unitId?: string | null; kind?: 1 | 2 | 3 | 4 | null }) => request<InventoryBarcode>("POST", `/api/v1/company/inventory/items/${enc(itemId)}/barcodes`, { body: barcode }),
    deactivateItemBarcode: (itemId: string, barcodeId: string) => request<InventoryBarcode>("POST", `/api/v1/company/inventory/items/${enc(itemId)}/barcodes/${enc(barcodeId)}/deactivate`),
    activateItemBarcode: (itemId: string, barcodeId: string) => request<InventoryBarcode>("POST", `/api/v1/company/inventory/items/${enc(itemId)}/barcodes/${enc(barcodeId)}/activate`),
    lookupBarcode: (barcode: string) => get<InventoryBarcodeLookup>(`/api/v1/company/inventory/barcode-lookup?barcode=${enc(barcode)}`),
    // Stock movement documents (Phase 3.5). Every change carries the document revision as If-Match; the server enforces every rule and every warehouse-scoped permission.
    getStockDocumentCapabilities: () => get<StockDocumentCapabilities>("/api/v1/company/stock-documents/capabilities"),
    listStockWarehouses: () => get<{ warehouses: StockWarehouseRef[] }>("/api/v1/company/stock-documents/warehouses"),
    searchStockItems: (search: string) => get<{ items: StockItemRef[] }>(`/api/v1/company/stock-documents/items?search=${enc(search)}`),
    listStockDocuments: (q: StockDocumentQuery = {}) => {
      const p = new URLSearchParams();
      if (q.type !== undefined) p.set("type", String(q.type));
      if (q.status !== undefined) p.set("status", String(q.status));
      if (q.warehouseId) p.set("warehouseId", q.warehouseId);
      if (q.from) p.set("from", q.from);
      if (q.to) p.set("to", q.to);
      if (q.search) p.set("search", q.search);
      if (q.offset) p.set("offset", String(q.offset));
      if (q.limit) p.set("limit", String(q.limit));
      const qs = p.toString();
      return get<{ documents: StockDocumentSummary[]; total: number }>(`/api/v1/company/stock-documents${qs ? "?" + qs : ""}`);
    },
    listStockBalanceWarehouses: () => get<{ warehouses: StockWarehouseRef[] }>("/api/v1/company/stock-balances/warehouses"),
    searchStockBalanceItems: (search: string) => get<{ items: StockBalanceItem[] }>(`/api/v1/company/stock-balances/items?search=${enc(search)}`),
    listStockBalances: (q: StockBalanceQuery = {}) => {
      const p = new URLSearchParams();
      if (q.itemId) p.set("itemId", q.itemId);
      if (q.warehouseId) p.set("warehouseId", q.warehouseId);
      if (q.search) p.set("search", q.search);
      if (q.asOf) p.set("asOf", q.asOf);
      if (q.includeZero) p.set("includeZero", "true");
      if (q.offset) p.set("offset", String(q.offset));
      if (q.limit) p.set("limit", String(q.limit));
      const qs = p.toString();
      return get<StockBalancePage>(`/api/v1/company/stock-balances${qs ? "?" + qs : ""}`);
    },
    getStockLedger: (q: StockLedgerQuery) => {
      const p = new URLSearchParams({ itemId: q.itemId, warehouseId: q.warehouseId });
      if (q.from) p.set("from", q.from);
      if (q.to) p.set("to", q.to);
      if (q.offset) p.set("offset", String(q.offset));
      if (q.limit) p.set("limit", String(q.limit));
      return get<StockLedgerPage>(`/api/v1/company/stock-balances/ledger?${p.toString()}`);
    },
    getStockReconciliation: (asOf: string) => get<StockReconciliation>(`/api/v1/company/stock-balances/reconciliation?asOf=${enc(asOf)}`),
    getStockDocument: (id: string) => get<StockDocument>(`/api/v1/company/stock-documents/${enc(id)}`),
    /** The execution of a document, or null while it has not been executed. */
    getStockPosting: async (id: string): Promise<StockPosting | null> => {
      try { return await get<StockPosting>(`/api/v1/company/stock-documents/${enc(id)}/posting`); }
      catch (e) { if (e instanceof ApiError && e.status === 404) return null; throw e; }
    },
    /** Posts an approved document: moves the stock, values it and creates the draft journal entry, atomically. A receipt needs the cost amount of every line. Idempotent. */
    postStockDocument: (id: string, revision: number, lines?: StockPostLineInput[]) => request<StockPosting>("POST", `/api/v1/company/stock-documents/${enc(id)}/posting`, { ifMatch: revision, body: { lines: lines ?? [] } }),
    reverseStockDocument: (id: string, effectiveDate: string, reason: string) => request<StockPosting>("POST", `/api/v1/company/stock-documents/${enc(id)}/reversal`, { body: { effectiveDate, reason } }),
    createStockDocument: (doc: StockDocumentInput) => request<StockDocument>("POST", "/api/v1/company/stock-documents", { body: doc }),
    updateStockDocument: (id: string, doc: StockDocumentInput, revision: number) => request<StockDocument>("PUT", `/api/v1/company/stock-documents/${enc(id)}`, { body: doc, ifMatch: revision }),
    submitStockDocument: (id: string, revision: number) => request<StockDocument>("POST", `/api/v1/company/stock-documents/${enc(id)}/submit`, { ifMatch: revision }),
    withdrawStockDocument: (id: string, revision: number) => request<StockDocument>("POST", `/api/v1/company/stock-documents/${enc(id)}/withdraw`, { ifMatch: revision }),
    approveStockDocument: (id: string, revision: number) => request<StockDocument>("POST", `/api/v1/company/stock-documents/${enc(id)}/approve`, { ifMatch: revision }),
    rejectStockDocument: (id: string, note: string, revision: number) => request<StockDocument>("POST", `/api/v1/company/stock-documents/${enc(id)}/reject`, { body: { note }, ifMatch: revision }),
    cancelStockDocument: (id: string, reason: string, revision: number) => request<StockDocument>("POST", `/api/v1/company/stock-documents/${enc(id)}/cancel`, { body: { reason }, ifMatch: revision }),
    // Stocktake (Phase 3.8B-1). Every change carries the stocktake revision as If-Match; the server enforces every rule and every warehouse-scoped permission.
    getStocktakeCapabilities: () => get<StocktakeCapabilities>("/api/v1/company/stocktakes/capabilities"),
    listStocktakeWarehouses: () => get<{ warehouses: StockWarehouseRef[] }>("/api/v1/company/stocktakes/warehouses"),
    searchStocktakeItems: (search: string) => get<{ items: StockItemRef[] }>(`/api/v1/company/stocktakes/items?search=${enc(search)}`),
    listStocktakes: (q: StocktakeQuery = {}) => {
      const p = new URLSearchParams();
      if (q.status !== undefined) p.set("status", String(q.status));
      if (q.warehouseId) p.set("warehouseId", q.warehouseId);
      if (q.from) p.set("from", q.from);
      if (q.to) p.set("to", q.to);
      if (q.search) p.set("search", q.search);
      if (q.offset) p.set("offset", String(q.offset));
      if (q.limit) p.set("limit", String(q.limit));
      const qs = p.toString();
      return get<{ stocktakes: StocktakeSummary[]; total: number }>(`/api/v1/company/stocktakes${qs ? "?" + qs : ""}`);
    },
    getStocktake: (id: string) => get<Stocktake>(`/api/v1/company/stocktakes/${enc(id)}`),
    createStocktake: (input: StocktakeInput) => request<Stocktake>("POST", "/api/v1/company/stocktakes", { body: input }),
    updateStocktake: (id: string, input: StocktakeInput, revision: number) => request<Stocktake>("PUT", `/api/v1/company/stocktakes/${enc(id)}`, { body: input, ifMatch: revision }),
    /** Adds the items with a non-zero book quantity (or exactly the chosen items) that are not on the sheet yet; existing counts are kept. */
    generateStocktakeSheet: (id: string, revision: number, itemIds?: string[]) => request<Stocktake>("POST", `/api/v1/company/stocktakes/${enc(id)}/generate-sheet`, { body: { itemIds: itemIds ?? null }, ifMatch: revision }),
    submitStocktake: (id: string, revision: number) => request<Stocktake>("POST", `/api/v1/company/stocktakes/${enc(id)}/submit`, { ifMatch: revision }),
    withdrawStocktake: (id: string, revision: number) => request<Stocktake>("POST", `/api/v1/company/stocktakes/${enc(id)}/withdraw`, { ifMatch: revision }),
    approveStocktake: (id: string, revision: number) => request<Stocktake>("POST", `/api/v1/company/stocktakes/${enc(id)}/approve`, { ifMatch: revision }),
    rejectStocktake: (id: string, note: string, revision: number) => request<Stocktake>("POST", `/api/v1/company/stocktakes/${enc(id)}/reject`, { body: { note }, ifMatch: revision }),
    cancelStocktake: (id: string, reason: string, revision: number) => request<Stocktake>("POST", `/api/v1/company/stocktakes/${enc(id)}/cancel`, { body: { reason }, ifMatch: revision }),
    // Party registry (Phase 3.9A). Changes carry the local party (or proposal) revision as If-Match; the company comes only from the session; the central queue is for identity administrators.
    getPartyCapabilities: () => get<PartyCapabilities>("/api/v1/company/parties/capabilities"),
    listParties: (q: { search?: string; linkState?: PartyLinkState; offset?: number; limit?: number } = {}) => {
      const qs = [q.search ? `search=${enc(q.search)}` : "", q.linkState ? `linkState=${q.linkState}` : "", q.offset !== undefined ? `offset=${q.offset}` : "", q.limit !== undefined ? `limit=${q.limit}` : ""].filter(Boolean).join("&");
      return get<{ parties: LocalPartySummary[]; total: number; registryUnavailable?: boolean }>(`/api/v1/company/parties${qs ? "?" + qs : ""}`);
    },
    getParty: (id: string) => get<LocalParty>(`/api/v1/company/parties/${enc(id)}`),
    createParty: (input: LocalPartyInput) => request<LocalParty>("POST", "/api/v1/company/parties", { body: input }),
    updateParty: (id: string, input: LocalPartyInput, revision: number) => request<LocalParty>("PUT", `/api/v1/company/parties/${enc(id)}`, { body: input, ifMatch: revision }),
    proposeParty: (id: string, identity: SharedIdentityInput, revision: number) => request<LocalParty>("POST", `/api/v1/company/parties/${enc(id)}/propose`, { body: identity, ifMatch: revision }),
    syncParties: () => request<PartySyncResult>("POST", "/api/v1/company/parties/sync"),
    reconcileParties: (repair: boolean) => request<PartyReconciliation>("POST", `/api/v1/company/parties/reconcile?repair=${repair}`),
    listPartyProposals: (status?: 1 | 2 | 3, offset = 0, limit = 25) => get<{ proposals: PartyProposalSummary[]; total: number }>(`/api/v1/admin/parties/proposals?offset=${offset}&limit=${limit}${status ? `&status=${status}` : ""}`),
    getPartyProposal: (id: string) => get<PartyProposal>(`/api/v1/admin/parties/proposals/${enc(id)}`),
    approvePartyProposal: (id: string, linkTo: string | null, revision: number) => request<PartyProposal>("POST", `/api/v1/admin/parties/proposals/${enc(id)}/approve`, { body: { linkTo }, ifMatch: revision }),
    rejectPartyProposal: (id: string, note: string, revision: number) => request<PartyProposal>("POST", `/api/v1/admin/parties/proposals/${enc(id)}/reject`, { body: { note }, ifMatch: revision }),
    searchSharedIdentities: (term: string) => get<{ identities: SharedIdentity[] }>(`/api/v1/admin/parties/search?term=${enc(term)}`),
    // Stock adjustment (Phase 3.8B-2). Generating carries the STOCKTAKE revision as If-Match and is idempotent; posting (surplus amounts) and reversal act once.
    generateStockAdjustment: (stocktakeId: string, revision: number) => request<StockAdjustment>("POST", `/api/v1/company/stocktakes/${enc(stocktakeId)}/adjustment`, { ifMatch: revision }),
    getStockAdjustment: (id: string) => get<StockAdjustment>(`/api/v1/company/stock-adjustments/${enc(id)}`),
    postStockAdjustment: (id: string, lines: StockPostLineInput[]) => request<StockAdjustment>("POST", `/api/v1/company/stock-adjustments/${enc(id)}/posting`, { body: { lines } }),
    reverseStockAdjustment: (id: string, effectiveDate: string, reason: string) => request<StockAdjustment>("POST", `/api/v1/company/stock-adjustments/${enc(id)}/reversal`, { body: { effectiveDate, reason } }),
    // Inventory accounting configuration (Phase 3.7A). The version number is the If-Match value (0 while nothing is configured).
    getInventoryAccounting: () => get<InventoryAccountingResponse>("/api/v1/company/inventory/accounting"),
    listInventoryAccountingVersions: () => get<{ versions: InventoryAccountingVersion[] }>("/api/v1/company/inventory/accounting/versions"),
    putInventoryAccounting: (config: InventoryAccountingInput, versionNo: number) => request<InventoryAccountingConfig>("PUT", "/api/v1/company/inventory/accounting", { body: config, ifMatch: versionNo }),
    // Negative-stock exception (Phase 3.8B-3): enablement (If-Match = version number), eligibility flags, the exception of an issue document (If-Match = document revision), obligations and the close report.
    getNegativeStockSettings: () => get<{ settings: NegativeStockSettings; canConfigure: boolean }>("/api/v1/company/negative-stock/settings"),
    putNegativeStockSettings: (input: { enabled: boolean; requireSecondApprover: boolean; note: string | null }, versionNo: number) => request<NegativeStockSettings>("PUT", "/api/v1/company/negative-stock/settings", { body: input, ifMatch: versionNo }),
    listNegativeItemFlags: () => get<{ items: NegativeItemFlag[] }>("/api/v1/company/negative-stock/items"),
    setNegativeItemFlag: (itemId: string, eligible: boolean, note: string | null) => request<NegativeItemFlag>("PUT", `/api/v1/company/negative-stock/items/${enc(itemId)}`, { body: { eligible, note } }),
    getNegativeException: (documentId: string) => get<{ exception: NegativeException; canApprove: boolean }>(`/api/v1/company/stock-documents/${enc(documentId)}/negative-exception`),
    approveNegativeException: (documentId: string, revision: number, reason: string, manualCosts: NegativeManualCost[]) =>
      request<NegativeException>("POST", `/api/v1/company/stock-documents/${enc(documentId)}/negative-exception/approvals`, { body: { reason, manualCosts }, ifMatch: revision }),
    listNegativeObligations: (status: "open" | "all" = "open") => get<{ obligations: NegativeObligation[] }>(`/api/v1/company/negative-stock/obligations?status=${status}`),
    getNegativeCloseReport: (periodEnd: string) => get<NegativeCloseReport>(`/api/v1/company/negative-stock/close-report?periodEnd=${enc(periodEnd)}`),    // Fiscal calendar (Phase 3.1D). Changes carry the revision of the year or period as If-Match; the server enforces every rule.
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
    // Generic import framework (3.2D). Every change carries the batch revision as If-Match; the upload sends the raw file (never multipart).
    importAdapters: () => get<ImportAdaptersResponse>("/api/v1/company/imports/adapters"),
    listImports: (q: { status?: ImportStatus; adapter?: string; limit?: number; offset?: number } = {}) => {
      const p = new URLSearchParams();
      if (q.status) p.set("status", q.status);
      if (q.adapter) p.set("adapter", q.adapter);
      if (q.limit) p.set("limit", String(q.limit));
      if (q.offset) p.set("offset", String(q.offset));
      const qs = p.toString();
      return get<{ imports: ImportBatch[] }>(`/api/v1/company/imports/${qs ? `?${qs}` : ""}`);
    },
    uploadImport: async (adapter: string, file: Blob, fileName: string, idempotencyKey?: string): Promise<ImportBatch> => {
      const headers: Record<string, string> = { Accept: "application/json", "X-Hasm-Request": "1", "Content-Type": "application/octet-stream" };
      if (idempotencyKey) headers["Idempotency-Key"] = idempotencyKey;
      if (kind === "desktop" && bearer) headers["Authorization"] = `Bearer ${bearer}`;
      const response = await fetchImpl(`${root}/api/v1/company/imports?adapter=${enc(adapter)}&fileName=${enc(fileName)}`, { method: "POST", headers, credentials: kind === "web" ? "include" : "omit", cache: "no-store", body: file });
      if (!response.ok) {
        let payload: { error?: string; fields?: { field: string; code: string }[] } = {};
        try { payload = (await response.json()) as typeof payload; } catch { /* non-JSON error body */ }
        if (response.status === 401) options.onUnauthenticated?.();
        throw new ApiError(response.status, payload.error ?? "request_failed", { fields: payload.fields });
      }
      return (await response.json()) as ImportBatch;
    },
    getImport: (id: string) => get<ImportBatch>(`/api/v1/company/imports/${enc(id)}`),
    stageImport: (id: string, o: ImportStageOptions, revision: number) => request<ImportBatch>("POST", `/api/v1/company/imports/${enc(id)}/stage`, { body: o, ifMatch: revision }),
    importRows: (id: string, q: { offset?: number; limit?: number; status?: number } = {}) =>
      get<ImportRowsPage>(`/api/v1/company/imports/${enc(id)}/rows?offset=${q.offset ?? 0}&limit=${q.limit ?? 100}${q.status ? `&status=${q.status}` : ""}`),
    importEvents: (id: string) => get<{ events: ImportEvent[] }>(`/api/v1/company/imports/${enc(id)}/events`),
    importFields: (id: string) => get<ImportFieldsResponse>(`/api/v1/company/imports/${enc(id)}/fields`),
    listImportTemplates: (adapter?: string) => get<{ templates: ImportTemplate[] }>(`/api/v1/company/imports/templates${adapter ? `?adapter=${enc(adapter)}` : ""}`),
    importTemplateHistory: (adapter: string, name: string) => get<{ versions: ImportTemplate[] }>(`/api/v1/company/imports/templates/${enc(adapter)}/${enc(name)}/history`),
    saveImportTemplate: (batchId: string, name: string, expectedVersion: number) => request<ImportTemplate>("POST", `/api/v1/company/imports/${enc(batchId)}/mapping/template`, { body: { name, expectedVersion } }),
    applyImportTemplate: (batchId: string, name: string) => request<ImportTemplateApplication>("POST", `/api/v1/company/imports/${enc(batchId)}/mapping/apply-template`, { body: { name } }),
    retireImportTemplate: (adapter: string, name: string, expectedVersion: number) => request<ImportTemplate>("POST", `/api/v1/company/imports/templates/${enc(adapter)}/${enc(name)}/retire`, { body: { expectedVersion } }),
    suggestImportMapping: (id: string) => get<{ mapping: ImportMapping }>(`/api/v1/company/imports/${enc(id)}/mapping/suggestion`),
    setImportMapping: (id: string, mapping: ImportMapping, revision: number) => request<ImportBatch>("PUT", `/api/v1/company/imports/${enc(id)}/mapping`, { body: mapping, ifMatch: revision }),
    dryRunImport: (id: string, revision: number) => request<ImportBatch>("POST", `/api/v1/company/imports/${enc(id)}/dry-run`, { ifMatch: revision }),
    setImportResolutions: (id: string, resolutions: ImportResolutionInput[], revision: number) => request<ImportBatch>("POST", `/api/v1/company/imports/${enc(id)}/resolutions`, { body: { resolutions }, ifMatch: revision }),
    checkImportPlan: (id: string) => get<{ fresh: boolean; currentPlanHash: string; approvedPlanHash: string | null; blocking: number }>(`/api/v1/company/imports/${enc(id)}/plan-check`),
    submitImport: (id: string, revision: number) => request<ImportBatch>("POST", `/api/v1/company/imports/${enc(id)}/submit`, { ifMatch: revision }),
    withdrawImport: (id: string, revision: number) => request<ImportBatch>("POST", `/api/v1/company/imports/${enc(id)}/withdraw`, { ifMatch: revision }),
    approveImport: (id: string, comment: string | null, revision: number) => request<ImportBatch>("POST", `/api/v1/company/imports/${enc(id)}/approve`, { body: { comment }, ifMatch: revision }),
    rejectImport: (id: string, comment: string, revision: number) => request<ImportBatch>("POST", `/api/v1/company/imports/${enc(id)}/reject`, { body: { comment }, ifMatch: revision }),
    applyImport: (id: string, revision: number) => request<ImportBatch>("POST", `/api/v1/company/imports/${enc(id)}/apply`, { ifMatch: revision }),
    cancelImport: (id: string, revision: number) => request<ImportBatch>("POST", `/api/v1/company/imports/${enc(id)}/cancel`, { ifMatch: revision }),
    renewImport: (id: string, revision: number) => request<ImportBatch>("POST", `/api/v1/company/imports/${enc(id)}/renew`, { ifMatch: revision }),
    importDecisions: (id: string) => get<{ decisions: ImportDecision[] }>(`/api/v1/company/imports/${enc(id)}/decisions`),
    getImportSettings: () => get<ImportSettingsResponse>("/api/v1/company/imports/settings"),
    putImportSettings: (retentionDays: number, pendingExpiryDays: number, revision: number) => request<ImportSettings>("PUT", "/api/v1/company/imports/settings", { body: { retentionDays, pendingExpiryDays }, ifMatch: revision }),
    runImportMaintenance: () => request<{ expired: number; purged: number; rewrapped: number }>("POST", "/api/v1/company/imports/maintenance/run"),
    getClosingSettings: () => get<ClosingSettingsResponse>("/api/v1/company/fiscal-closing/settings"),
    putClosingSettings: (body: { equityAccountId: string }, revision: number) => request<ClosingSettings>("PUT", "/api/v1/company/fiscal-closing/settings", { body, ifMatch: revision }),
    listYearEnd: () => get<{ years: YearEndStatus[] }>("/api/v1/company/fiscal-closing/years"),
    previewYearEnd: (yearId: string) => get<ClosingPreview>(`/api/v1/company/fiscal-closing/years/${enc(yearId)}/preview`),
    startYearClosing: (yearId: string) => request<FiscalYearClosing>("POST", `/api/v1/company/fiscal-closing/years/${enc(yearId)}/close`),
    carryForwardYear: (yearId: string) => request<FiscalYearCarryforward>("POST", `/api/v1/company/fiscal-closing/years/${enc(yearId)}/carry-forward`),
    reverseYearOpening: (yearId: string, entryDate?: string) => request<{ entryId: string; status: string }>("POST", `/api/v1/company/fiscal-closing/years/${enc(yearId)}/reverse-opening`, { body: { entryDate: entryDate ?? null } }),
    reverseYearClosing: (yearId: string, entryDate?: string) => request<{ entryId: string; status: string }>("POST", `/api/v1/company/fiscal-closing/years/${enc(yearId)}/reverse-closing`, { body: { entryDate: entryDate ?? null } }),
    listAllocatableCostCenters: () => get<{ costCenters: AllocatableCostCenter[] }>("/api/v1/company/journal/cost-centers"),
    listRequiredCostCenterAccounts: () => get<AccountCostCenterRulesResponse>("/api/v1/company/cost-center-policies/accounts"),
    getAccountCostCenterRule: (accountId: string) => get<AccountCostCenterRule>(`/api/v1/company/cost-center-policies/accounts/${enc(accountId)}`),
    setAccountCostCenterRule: (accountId: string, required: boolean, revision: number) =>
      request<AccountCostCenterRule>("PUT", `/api/v1/company/cost-center-policies/accounts/${enc(accountId)}`, { body: { required }, ifMatch: revision }),
    listRestrictedCostCenters: () => get<CostCenterBranchRulesResponse>("/api/v1/company/cost-center-policies/branches"),
    getCostCenterBranchRule: (costCenterId: string) => get<CostCenterBranchRule>(`/api/v1/company/cost-center-policies/branches/${enc(costCenterId)}`),
    setCostCenterBranchRule: (costCenterId: string, rule: { restricted: boolean; branchIds: string[] }, revision: number) =>
      request<CostCenterBranchRule>("PUT", `/api/v1/company/cost-center-policies/branches/${enc(costCenterId)}`, { body: rule, ifMatch: revision }),
    getAttachmentCapabilities: () => get<AttachmentCapabilities>("/api/v1/company/attachments/capabilities"),
    listAttachments: (subjectKind: AttachmentSubjectKind, subjectId: string) => get<AttachmentsResponse>(`/api/v1/company/attachments?subjectKind=${enc(subjectKind)}&subjectId=${enc(subjectId)}`),
    uploadAttachment: async (subjectKind: AttachmentSubjectKind, subjectId: string, file: Blob, fileName: string, sensitive: boolean): Promise<Attachment> => {
      const form = new FormData();
      form.append("subjectKind", subjectKind); form.append("subjectId", subjectId); form.append("sensitive", sensitive ? "true" : "false"); form.append("file", file, fileName);
      const headers: Record<string, string> = { Accept: "application/json", "X-Hasm-Request": "1" };      // the browser sets the multipart boundary
      if (kind === "desktop" && bearer) headers["Authorization"] = `Bearer ${bearer}`;
      const response = await fetchImpl(`${root}/api/v1/company/attachments`, { method: "POST", headers, credentials: kind === "web" ? "include" : "omit", cache: "no-store", body: form });
      if (!response.ok) {
        let payload: { error?: string; fields?: { field: string; code: string }[] } = {};
        try { payload = (await response.json()) as typeof payload; } catch { /* non-JSON error body */ }
        if (response.status === 401) options.onUnauthenticated?.();
        throw new ApiError(response.status, payload.error ?? "request_failed", { fields: payload.fields });
      }
      return (await response.json()) as Attachment;
    },
    downloadAttachment: (id: string) => download(`/api/v1/company/attachments/${enc(id)}/content`, "*/*"),
    removeAttachment: (id: string, reason: string, revision: number) => request<Attachment>("POST", `/api/v1/company/attachments/${enc(id)}/remove`, { body: { reason }, ifMatch: revision }),
    /** Without `q` the list is unchanged (all roles, no `total`). With `search` (party code or name), `limit` (default 50, at most 500) or `offset` the server answers one page and adds `total`. */
    listPartyRoles: (kind?: number, q?: PartyRoleQuery) => {
      const p = new URLSearchParams();
      if (kind) p.set("kind", String(kind));
      if (q?.search) p.set("search", q.search);
      if (q?.limit !== undefined) p.set("limit", String(q.limit));
      if (q?.offset !== undefined) p.set("offset", String(q.offset));
      const qs = p.toString();
      return get<PartyRolesResponse>(`/api/v1/company/party-roles${qs ? "?" + qs : ""}`);
    },
    createPartyRole: (r: PartyRoleInput) => request<PartyRole>("POST", "/api/v1/company/party-roles", { body: r }),
    updatePartyRole: (id: string, r: PartyRoleInput, revision: number) => request<PartyRole>("PUT", `/api/v1/company/party-roles/${enc(id)}`, { body: r, ifMatch: revision }),
    listSalesRepresentatives: () => get<SalesRepresentativesResponse>("/api/v1/company/sales-representatives"),
    createSalesRepresentative: (r: SalesRepresentativeInput) => request<SalesRepresentative>("POST", "/api/v1/company/sales-representatives", { body: r }),
    updateSalesRepresentative: (id: string, r: SalesRepresentativeInput, revision: number) => request<SalesRepresentative>("PUT", `/api/v1/company/sales-representatives/${enc(id)}`, { body: r, ifMatch: revision }),
    listTaxes: () => get<TaxesResponse>("/api/v1/company/taxes"),
    createTax: (t: TaxInput) => request<Tax>("POST", "/api/v1/company/taxes", { body: t }),
    updateTax: (id: string, t: TaxInput, revision: number) => request<Tax>("PUT", `/api/v1/company/taxes/${enc(id)}`, { body: t, ifMatch: revision }),
    addTaxRate: (id: string, rate: TaxRateInput) => request<Tax>("POST", `/api/v1/company/taxes/${enc(id)}/rates`, { body: rate }),
    listTaxClasses: () => get<TaxClassesResponse>("/api/v1/company/taxes/classes"),
    createTaxClass: (c: TaxClassInput) => request<TaxClass>("POST", "/api/v1/company/taxes/classes", { body: c }),
    updateTaxClass: (id: string, c: TaxClassInput, revision: number) => request<TaxClass>("PUT", `/api/v1/company/taxes/classes/${enc(id)}`, { body: c, ifMatch: revision }),
    listItemTaxClasses: () => get<TaxAssignmentsResponse>("/api/v1/company/taxes/item-classes"),
    listPartyTaxClasses: () => get<TaxAssignmentsResponse>("/api/v1/company/taxes/party-classes"),
    assignItemTaxClass: (itemId: string, classId: string | null) => request<{ ok: boolean }>("PUT", `/api/v1/company/taxes/item-classes/${enc(itemId)}`, { body: { classId } }),
    assignPartyTaxClass: (partyId: string, classId: string | null) => request<{ ok: boolean }>("PUT", `/api/v1/company/taxes/party-classes/${enc(partyId)}`, { body: { classId } }),
    listTaxRules: () => get<TaxRulesResponse>("/api/v1/company/taxes/rules"),
    createTaxRule: (r: TaxRuleInput) => request<TaxRule>("POST", "/api/v1/company/taxes/rules", { body: r }),
    updateTaxRule: (id: string, r: TaxRuleInput, revision: number) => request<TaxRule>("PUT", `/api/v1/company/taxes/rules/${enc(id)}`, { body: r, ifMatch: revision }),
    getTaxPolicy: () => get<TaxPolicyResponse>("/api/v1/company/taxes/policy"),
    addTaxPolicy: (p: TaxPolicyInput) => request<TaxPolicy>("POST", "/api/v1/company/taxes/policy", { body: p }),
    resolveTaxes: (q: { documentType: string; operation: string; taxDate: string; itemId?: string; partyId?: string }) =>
      get<TaxResolution>(`/api/v1/company/taxes/resolve?documentType=${enc(q.documentType)}&operation=${enc(q.operation)}&taxDate=${enc(q.taxDate)}${q.itemId ? `&itemId=${enc(q.itemId)}` : ""}${q.partyId ? `&partyId=${enc(q.partyId)}` : ""}`),
    getPricingPolicy: () => get<PricingPolicyResponse>("/api/v1/company/pricing/policy"),
    savePricingPolicy: (p: PricingPolicyInput, version: number) => request<PricingPolicy>("POST", "/api/v1/company/pricing/policy", { body: p, ifMatch: version }),
    listPriceLists: () => get<PriceListsResponse>("/api/v1/company/pricing/price-lists"),
    createPriceList: (l: PriceListInput) => request<PriceList>("POST", "/api/v1/company/pricing/price-lists", { body: l }),
    updatePriceList: (id: string, l: PriceListInput, revision: number) => request<PriceList>("PUT", `/api/v1/company/pricing/price-lists/${enc(id)}`, { body: l, ifMatch: revision }),
    listPriceEntries: (listId: string) => get<PriceEntriesResponse>(`/api/v1/company/pricing/price-lists/${enc(listId)}/entries`),
    createPriceEntry: (listId: string, e: PriceEntryInput) => request<PriceEntry>("POST", `/api/v1/company/pricing/price-lists/${enc(listId)}/entries`, { body: e }),
    updatePriceEntry: (id: string, e: PriceEntryInput, revision: number) => request<PriceEntry>("PUT", `/api/v1/company/pricing/entries/${enc(id)}`, { body: e, ifMatch: revision }),
    listDiscountRules: () => get<DiscountRulesResponse>("/api/v1/company/pricing/discounts"),
    createDiscountRule: (d: DiscountRuleInput) => request<DiscountRule>("POST", "/api/v1/company/pricing/discounts", { body: d }),
    updateDiscountRule: (id: string, d: DiscountRuleInput, revision: number) => request<DiscountRule>("PUT", `/api/v1/company/pricing/discounts/${enc(id)}`, { body: d, ifMatch: revision }),
    listCustomerGroups: () => get<CustomerGroupsResponse>("/api/v1/company/pricing/customer-groups"),
    createCustomerGroup: (g: CustomerGroupInput) => request<CustomerGroup>("POST", "/api/v1/company/pricing/customer-groups", { body: g }),
    updateCustomerGroup: (id: string, g: CustomerGroupInput, revision: number) => request<CustomerGroup>("PUT", `/api/v1/company/pricing/customer-groups/${enc(id)}`, { body: g, ifMatch: revision }),
    listCustomerGroupAssignments: () => get<CustomerGroupAssignmentsResponse>("/api/v1/company/pricing/customer-assignments"),
    setCustomerGroupAssignment: (partyId: string, customerGroupId: string | null, revision: number) => request<CustomerGroupAssignment>("PUT", `/api/v1/company/pricing/customer-assignments/${enc(partyId)}`, { body: { customerGroupId }, ifMatch: revision }),
    quotePrice: (q: PricingQuoteRequest) => request<PricingQuote>("POST", "/api/v1/company/pricing/quote", { body: q }),
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
    /** Any export format (xlsx, pdf, csv) of the trial balance: the COMPLETE authorized result with the same filters, comparison, depth, sort and column selection as the screen. */
    exportTrialBalance: (q: ReportQuery & { includeZero?: boolean }, format: ReportFormat) => download(`/api/v1/company/reports/trial-balance?${reportQuery(q)}${q.includeZero ? "&includeZero=true" : ""}&format=${format}`),
    exportGeneralLedger: (accountId: string, q: ReportQuery, format: ReportFormat) => download(`/api/v1/company/reports/general-ledger?accountId=${enc(accountId)}&${reportQuery(q)}&format=${format}`),
    getStatement: (kind: StatementKind, q: StatementQuery) => get<StatementResponse>(`/api/v1/company/statements/${kind}?${reportQuery(q)}${q.mappingVersion ? `&mappingVersion=${q.mappingVersion}` : ""}`),
    exportStatement: (kind: StatementKind, q: StatementQuery, format: ReportFormat) => download(`/api/v1/company/statements/${kind}?${reportQuery(q)}${q.mappingVersion ? `&mappingVersion=${q.mappingVersion}` : ""}&format=${format}`),
    getStatementMapping: (version?: number) => get<StatementMappingResponse>(`/api/v1/company/statements/mapping${version ? `?version=${version}` : ""}`),
    listStatementMappingVersions: () => get<{ versions: StatementMappingVersionInfo[] }>("/api/v1/company/statements/mapping/versions"),
    getStatementMappingSuggestion: () => get<{ entries: { accountId: string; section: string }[] }>("/api/v1/company/statements/mapping/suggestion"),
    saveStatementMapping: (entries: { accountId: string; section: string }[], expectedVersion: number, note: string | null, source: "manual" | "template" = "manual") =>
      request<{ version: StatementMappingVersionInfo | null; entries: { accountId: string; section: string }[] }>("PUT", "/api/v1/company/statements/mapping", { body: { entries, note, source }, ifMatch: expectedVersion }),
    getReportDefinitions: () => get<{ reports: ReportDefinitionInfo[] }>("/api/v1/company/reports/definitions"),
    getReportPeriod: (q: { fiscalYearId?: string; fiscalPeriodId?: string; from?: string; to?: string; comparison?: ReportComparison }) =>
      get<ReportPeriodInfo>(`/api/v1/company/reports/period?${[q.fiscalPeriodId ? `fiscalPeriodId=${enc(q.fiscalPeriodId)}` : "", q.fiscalYearId ? `fiscalYearId=${enc(q.fiscalYearId)}` : "", q.from ? `from=${enc(q.from)}` : "", q.to ? `to=${enc(q.to)}` : "", q.comparison && q.comparison !== "none" ? `comparison=${q.comparison}` : ""].filter(Boolean).join("&")}`),
    getReconciliation: (q: ReportQuery) => get<ReconciliationResponse>(`/api/v1/company/reports/reconciliation?${reportQuery(q)}`),
  };
}

export type ApiClient = ReturnType<typeof createApiClient>;
