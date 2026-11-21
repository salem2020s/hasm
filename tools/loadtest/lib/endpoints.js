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
  journalList: ["GET", `${C}/journal`],
  journalCreate: ["POST", `${C}/journal`],
  journalSubmit: ["POST", `${C}/journal/{id}/submit`],
  journalPost: ["POST", `${C}/journal/{id}/post`],
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
const path = (name, params = {}) => ENDPOINTS[name][1].replace(/\{(\w+)\}/g, (_, k) => encodeURIComponent(params[k]));
module.exports = { ENDPOINTS, BODIES, path };
