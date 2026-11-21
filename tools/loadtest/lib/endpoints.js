"use strict";
// Every API route and request body the harness uses, in one place. tests/smoke.test.js checks each route against the backend route table and
// each body field against the backend request records, so a renamed route or field fails the test instead of silently breaking a run.
const C = "/api/v1/company";
const ENDPOINTS = {
  login: ["POST", "/api/v1/auth/login"],
  selectCompany: ["POST", "/api/v1/session/company"],
  health: ["GET", "/api/v1/health"],
  units: ["GET", `${C}/inventory/units`],
  createUnit: ["POST", `${C}/inventory/units`],
  groups: ["GET", `${C}/inventory/groups`],
  createGroup: ["POST", `${C}/inventory/groups`],
  items: ["GET", `${C}/inventory/items`],
  createItem: ["POST", `${C}/inventory/items`],
  parties: ["GET", `${C}/parties`],
  createParty: ["POST", `${C}/parties`],
  partyRoles: ["GET", `${C}/party-roles`],
  createPartyRole: ["POST", `${C}/party-roles`],
  branches: ["GET", `${C}/branches`],
  warehouses: ["GET", `${C}/warehouses`],
  accounts: ["GET", `${C}/accounts`],
  journalGet: ["GET", `${C}/journal/{id}`],
  journalList: ["GET", `${C}/journal`],
  journalCreate: ["POST", `${C}/journal`],
  journalSubmit: ["POST", `${C}/journal/{id}/submit`],
  journalPost: ["POST", `${C}/journal/{id}/post`],
  stockDocGet: ["GET", `${C}/stock-documents/{id}`],
  stockDocList: ["GET", `${C}/stock-documents`],
  stockDocCreate: ["POST", `${C}/stock-documents`],
  stockDocSubmit: ["POST", `${C}/stock-documents/{id}/submit`],
  stockDocApprove: ["POST", `${C}/stock-documents/{id}/approve`],
  stockBalances: ["GET", `${C}/stock-balances`],
  trialBalance: ["GET", `${C}/reports/trial-balance`],
  generalLedger: ["GET", `${C}/reports/general-ledger`],
};

// Request-body fields per backend record (camelCase JSON names). Checked against the C# records by the smoke test.
const BODIES = {
  LoginRequest: ["login", "password", "client"],
  UnitInput: ["code", "nameAr", "nameEn", "decimals"],
  ItemGroupInput: ["code", "nameAr", "nameEn"],
  ItemInput: ["code", "nameAr", "nameEn", "itemType", "groupId", "baseUnitId"],
  LocalPartyInput: ["code", "displayName", "identity"],
  SharedIdentityInput: ["kind", "legalNameAr", "legalNameEn"],
  PartyRoleInput: ["partyId", "role", "paymentMode"],
  JournalInput: ["entryDate", "description", "branchId", "lines"],
  JournalLineInput: ["accountId", "description", "debit", "credit"],
  StockDocumentInput: ["docType", "sourceWarehouseId", "targetWarehouseId", "documentDate", "reference", "lines"],
  StockLineInput: ["itemId", "unitId", "quantity"],
};
// Query parameters per GET route (checked against the backend handler signatures), enum values (checked against the backend enums) and the
// response keys the harness reads (checked against the backend endpoint sources).
const QUERIES = {
  items: ["search", "limit", "offset"], parties: ["search", "limit", "offset"], partyRoles: ["kind", "search", "limit"],
  journalList: ["q", "limit", "offset"], stockDocList: ["search", "limit", "offset"],
  trialBalance: ["from", "to"], generalLedger: ["accountId", "from", "to", "limit"],
};
const ENUMS = [ // [C# enum, member, value used by the harness]
  ["StockDocType", "Receipt", 1], ["StockDocType", "Issue", 2], ["StockDocType", "Transfer", 3], ["StockDocStatus", "Draft", 1], ["StockDocStatus", "Submitted", 2], ["StockDocStatus", "Approved", 3],
  ["PartyRoleKind", "Customer", 1], ["PartyPaymentMode", "Cash", 1], ["PartyKind", "Organization", 1],
];
const RESPONSE_KEYS = { // [source file under src/backend/Hasm.Api, keys that must appear as `key =` in an anonymous response object]
  "Auth/AuthEndpoints.cs": ["token"], "Company/InventoryEndpoints.cs": ["items", "total", "units", "groups", "id", "code", "revision"],
  "Company/PartyEndpoints.cs": ["parties", "total", "id", "code", "revision"], "Company/PartyRoleEndpoints.cs": ["items"],
  "Company/BranchEndpoints.cs": ["branches", "isMain"], "Company/WarehouseEndpoints.cs": ["warehouses", "isActive"],
  "Company/AccountEndpoints.cs": ["accounts", "postable", "isActive", "currencyPolicy"],
  "Company/JournalEndpoints.cs": ["entry", "entries", "id", "revision", "status", "description"],
  "Company/StockDocumentEndpoints.cs": ["documents", "total", "id", "status", "revision", "reference"],
};
const path = (name, params = {}) => ENDPOINTS[name][1].replace(/\{(\w+)\}/g, (_, k) => encodeURIComponent(params[k]));
module.exports = { ENDPOINTS, BODIES, QUERIES, ENUMS, RESPONSE_KEYS, path };
