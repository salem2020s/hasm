import { ApiError, type ApiClient } from "@hasm/api-client";
import { I18nProvider } from "@hasm/ui";
import { render } from "@testing-library/react";
import { vi } from "vitest";
import { App } from "./App";
import { catalog } from "./messages";

export const ALL_PERMISSIONS = [
  "company.profile.view", "company.profile.update", "company.settings.view", "company.settings.update",
  "company.currencies.view", "company.currencies.update", "company.config.view",
];

export const USER = { id: "u1", login: "amal", displayName: "Amal", isIdentityAdministrator: false };

export function fakeClient(overrides: Partial<Record<keyof ApiClient, unknown>> = {}, permissions: string[] = ALL_PERMISSIONS): ApiClient {
  const base = {
    getHealth: vi.fn().mockResolvedValue({ status: "healthy", service: "hasm-api", version: "1" }),
    login: vi.fn().mockResolvedValue({ user: USER }),
    logout: vi.fn().mockResolvedValue(undefined),
    status: vi.fn().mockResolvedValue({ authenticated: false }),
    listCompanies: vi.fn().mockResolvedValue({ companies: [{ id: "c1", nameAr: "شركة الأمل", nameEn: "Amal Co" }] }),
    selectCompany: vi.fn().mockResolvedValue({ companyId: "c1" }),
    clearCompany: vi.fn().mockResolvedValue(undefined),
    accessMe: vi.fn().mockResolvedValue({ companyId: "c1", grants: [{ roleId: "r", scope: "company", scopeId: null, permissions }] }),
    getProfile: vi.fn().mockResolvedValue({ profile: null, revision: 0, updatedAtUtc: null, projection: "none" }),
    putProfile: vi.fn(),
    syncDisplayName: vi.fn(),
    getSettings: vi.fn().mockResolvedValue({
      settings: { defaultLanguage: "ar", dateFormat: "yyyy-MM-dd", numeralSystem: "latin", timeZone: null },
      revision: 0, exists: false, setupComplete: false, updatedAtUtc: null,
      options: { languages: ["ar", "en"], dateFormats: ["yyyy-MM-dd", "dd/MM/yyyy", "MM/dd/yyyy"], numeralSystems: ["latin", "arabic-indic"] },
    }),
    putSettings: vi.fn(),
    getCurrencies: vi.fn().mockResolvedValue({
      currencies: [], baseCurrency: null, baseCurrencySelected: false,
      baseCurrencyLock: { locked: false, checksImplemented: false, unimplementedDomains: ["general_ledger"] },
    }),
    getCurrencyReference: vi.fn().mockResolvedValue({
      currencies: [
        { code: "SAR", numericCode: "682", nameEn: "Saudi Riyal", minorUnits: 2, active: true, datasetVersion: 1 },
        { code: "USD", numericCode: "840", nameEn: "US Dollar", minorUnits: 2, active: true, datasetVersion: 1 },
      ],
    }),
    setCurrency: vi.fn(),
    getHistory: vi.fn().mockResolvedValue({ entries: [] }),
    getEntitlements: vi.fn().mockResolvedValue({ edition: "trial", restrictions: [{ key: "multi_currency", limit: 1, enforced: true, note: "" }] }),
    listBranches: vi.fn().mockResolvedValue({ branches: [], limit: null, everCreated: null, can: { create: false, activate: false, designate: false, update: [] } }),
    getBranch: vi.fn(),
    createBranch: vi.fn(),
    updateBranch: vi.fn(),
    deactivateBranch: vi.fn(),
    activateBranch: vi.fn(),
    makeMainBranch: vi.fn(),
  };
  return { ...base, ...overrides } as unknown as ApiClient;
}

/** A client whose session is already established with a selected company. */
export const signedIn = (overrides: Partial<Record<keyof ApiClient, unknown>> = {}, permissions?: string[]) =>
  fakeClient({ status: vi.fn().mockResolvedValue({ authenticated: true, user: USER }), ...overrides }, permissions);

export const renderApp = (client: ApiClient) =>
  render(<I18nProvider catalog={catalog}><App client={client} /></I18nProvider>);

export const apiError = (status: number, code: string, details: ConstructorParameters<typeof ApiError>[2] = {}) => new ApiError(status, code, details);
