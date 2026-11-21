import type { Page, Route } from "@playwright/test";

/**
 * A stateful in-memory stand-in for the HASM API, installed with page.route() on the address the production bundle was built for (VITE_API_BASE_URL in playwright.config.ts).
 * It exists to run the REAL built bundle through complete flows without a backend or a database, for BOTH client transports:
 *  - "web": the HttpOnly session cookie. The mock sets it on login (Set-Cookie) and accepts it on every later call.
 *  - "desktop": an opaque bearer token returned by login and sent in the Authorization header; no cookie is ever sent.
 * The mock refuses a call that carries the other transport's credential, so a client that mixed them up fails the flow. Every request is recorded so the tests can compare what each client actually sent.
 * It is test infrastructure only: it implements no business rule (the real rules live in the backend and are covered by its own tests).
 */
export type Mode = "web" | "desktop";
export interface Recorded { method: string; path: string; cookie: boolean; bearer: boolean; csrfHeader: boolean; credentialsPath: "cookie" | "bearer" | "none" }

const ORIGIN = "http://127.0.0.1:4173";
export const API = "http://127.0.0.1:9";
const USER = { id: "u1", login: "amal", displayName: "Amal", isIdentityAdministrator: false };
export const PASSWORD = "correct-horse";

const baseAccount = { nature: "debit", statement: "balance_sheet", isGroup: false, isActive: true, postable: true, hasChildren: false, currencyPolicy: "any", currencyCode: null, template: null, revision: 1, createdAtUtc: "2026-01-01T00:00:00Z", updatedAtUtc: "2026-01-01T00:00:00Z", parentId: null };
const ACCOUNTS = [
  { ...baseAccount, id: "a1", code: "1101", nameAr: "الصندوق", nameEn: "Cash", type: "asset" },
  { ...baseAccount, id: "a2", code: "4101", nameAr: "المبيعات", nameEn: "Sales", type: "revenue", nature: "credit", statement: "profit_and_loss" },
];
const FULL = ["company.profile.view", "company.settings.view", "company.currencies.view", "company.config.view", "company.accounts.view", "company.accounts.create", "company.journal.view", "company.journal.create", "company.branches.view"];
const COMPANIES = [
  { id: "c1", nameAr: "شركة الأمل", nameEn: "Amal Co", permissions: FULL },
  { id: "c2", nameAr: "شركة محدودة", nameEn: "Limited Co", permissions: ["company.profile.view"] },     // no accounts or journal permission: the forbidden screens
];

export class MockApi {
  readonly requests: Recorded[] = [];
  readonly unhandled: string[] = [];
  readonly created: { accounts: unknown[]; journal: unknown[] } = { accounts: [], journal: [] };
  private token: string | null = null;
  private companyId: string | null = null;
  private accounts: Record<string, unknown>[] = [...ACCOUNTS];
  private entries: { entry: Record<string, unknown>; lines: unknown[] }[] = [];

  constructor(readonly mode: Mode) {}

  /** True while a session exists. The next authenticated call after expire() is answered 401, exactly like an expired or revoked session. */
  get signedIn(): boolean { return this.token !== null; }
  expire(): void { this.token = null; this.companyId = null; }

  async install(page: Page): Promise<void> {
    if (this.mode === "desktop") await page.addInitScript(() => { (window as unknown as Record<string, unknown>)["__TAURI_INTERNALS__"] = { metadata: { currentWindow: { label: "main" }, currentWebview: { label: "main", windowLabel: "main" } }, invoke: async () => undefined, transformCallback: () => 0 }; });
    await page.route(`${API}/**`, (route) => this.handle(route));
  }

  private cors(extra: Record<string, string> = {}): Record<string, string> {
    return { "access-control-allow-origin": ORIGIN, "access-control-allow-credentials": "true", "access-control-allow-headers": "content-type,x-hasm-request,authorization,if-match,accept", "access-control-allow-methods": "GET,POST,PUT,DELETE,OPTIONS", vary: "Origin", ...extra };
  }
  private json(route: Route, status: number, body: unknown, headers: Record<string, string> = {}) {
    return route.fulfill({ status, contentType: "application/json", headers: this.cors(headers), body: JSON.stringify(body) });
  }

  private async handle(route: Route): Promise<void> {
    const request = route.request();
    if (request.method() === "OPTIONS") return route.fulfill({ status: 204, headers: this.cors() });
    const headers = await request.allHeaders();
    const url = new URL(request.url());
    const path = url.pathname;
    const method = request.method();
    const cookieValue = (headers["cookie"] ?? "").split(";").map((c) => c.trim()).find((c) => c.startsWith("hasm_session="))?.slice("hasm_session=".length) ?? null;
    const bearerValue = headers["authorization"]?.startsWith("Bearer ") ? headers["authorization"].slice(7) : null;
    this.requests.push({ method, path, cookie: cookieValue !== null, bearer: bearerValue !== null, csrfHeader: headers["x-hasm-request"] === "1", credentialsPath: cookieValue !== null ? "cookie" : bearerValue !== null ? "bearer" : "none" });

    // ---- anonymous endpoints
    if (path === "/api/v1/health") return this.json(route, 200, { status: "healthy", service: "hasm-api", version: "e2e" });
    if (path === "/api/v1/auth/login" && method === "POST") {
      if (headers["x-hasm-request"] !== "1") return this.json(route, 403, { error: "csrf" });                    // the anti-CSRF header is required on login too
      const body = JSON.parse(request.postData() ?? "{}") as { login?: string; password?: string; client?: string };
      if (body.client !== this.mode) return this.json(route, 400, { error: "invalid_request" });                 // the client announces its transport
      if (body.login !== USER.login || body.password !== PASSWORD) return this.json(route, 401, { error: "invalid_credentials" });
      this.token = `tok-${Math.random().toString(36).slice(2)}`;
      return this.mode === "web"
        ? this.json(route, 200, { user: USER }, { "set-cookie": `hasm_session=${this.token}; Path=/; HttpOnly; SameSite=Lax` })
        : this.json(route, 200, { user: USER, token: this.token });
    }
    const presented = this.mode === "web" ? cookieValue : bearerValue;
    const foreign = this.mode === "web" ? bearerValue : cookieValue;
    const authenticated = this.token !== null && presented === this.token && foreign === null;
    if (path === "/api/v1/auth/status") return this.json(route, 200, authenticated ? { authenticated: true, user: USER } : { authenticated: false });
    if (path === "/api/v1/auth/logout" && method === "POST") {
      if (headers["x-hasm-request"] !== "1") return this.json(route, 403, { error: "csrf" });
      this.expire(); return route.fulfill({ status: 204, headers: this.cors(this.mode === "web" ? { "set-cookie": "hasm_session=; Path=/; Max-Age=0" } : {}) });
    }

    // ---- everything else needs the session (and the anti-CSRF header on state changes)
    if (!authenticated) return this.json(route, 401, { error: "unauthenticated" });
    if (method !== "GET" && headers["x-hasm-request"] !== "1") return this.json(route, 403, { error: "csrf" });

    if (path === "/api/v1/companies") return this.json(route, 200, { companies: COMPANIES.map(({ id, nameAr, nameEn }) => ({ id, nameAr, nameEn })) });
    if (path === "/api/v1/session/company" && method === "POST") {
      const { companyId } = JSON.parse(request.postData() ?? "{}") as { companyId?: string };
      if (!COMPANIES.some((c) => c.id === companyId)) return this.json(route, 404, { error: "company_not_found" });
      this.companyId = companyId!;
      return this.json(route, 200, { companyId });
    }
    if (path === "/api/v1/session/company" && method === "DELETE") { this.companyId = null; return route.fulfill({ status: 204, headers: this.cors() }); }
    const company = COMPANIES.find((c) => c.id === this.companyId);
    if (path === "/api/v1/access/me") return company ? this.json(route, 200, { companyId: company.id, grants: [{ roleId: "r", scope: "company", scopeId: null, permissions: company.permissions }] }) : this.json(route, 409, { error: "company_not_selected" });
    if (!company) return this.json(route, 409, { error: "company_not_selected" });

    const can = (p: string) => company.permissions.includes(p);
    if (path === "/api/v1/company/accounts" && method === "GET") {
      if (!can("company.accounts.view")) return this.json(route, 403, { error: "forbidden" });
      return this.json(route, 200, { accounts: this.accounts, settings: { segmentWidth: 2, autoCodes: true, revision: 0, configured: false }, templates: [], usageChecksImplemented: false, can: { create: can("company.accounts.create"), update: false, activate: false, recode: false, restructure: false, configure: false } });
    }
    if (path === "/api/v1/company/accounts" && method === "POST") {
      if (!can("company.accounts.create")) return this.json(route, 403, { error: "forbidden" });
      const input = JSON.parse(request.postData() ?? "{}") as Record<string, unknown>;
      const account = { ...baseAccount, id: `a${this.accounts.length + 1}`, code: input["code"] ?? String(this.accounts.length + 100), nameAr: input["nameAr"] ?? null, nameEn: input["nameEn"] ?? null, type: input["type"], nature: input["nature"] ?? "debit", statement: input["statement"] ?? "balance_sheet", isGroup: !!input["isGroup"], postable: !input["isGroup"], currencyPolicy: input["currencyPolicy"] ?? "base" };
      this.accounts.push(account); this.created.accounts.push(input);
      return this.json(route, 201, account);
    }
    if (path === "/api/v1/company/settings") return this.json(route, 200, { settings: { defaultLanguage: "ar", dateFormat: "yyyy-MM-dd", numeralSystem: "latin", timeZone: null }, revision: 0, exists: false, setupComplete: false, updatedAtUtc: null, options: { languages: ["ar", "en"], dateFormats: ["yyyy-MM-dd"], numeralSystems: ["latin", "arabic-indic"] } });
    if (path === "/api/v1/company/profile") return this.json(route, 200, { profile: null, revision: 0, updatedAtUtc: null, projection: "none" });
    if (path === "/api/v1/company/entitlements") return this.json(route, 200, { edition: "trial", restrictions: [] });
    if (path === "/api/v1/company/history") return this.json(route, 200, { entries: [] });
    if (path === "/api/v1/company/currencies") return this.json(route, 200, { currencies: [{ code: "JOD", nameEn: "Dinar", minorUnits: 3, isBase: true, enabled: true, revision: 1 }], baseCurrency: "JOD", baseCurrencySelected: true, baseCurrencyLock: { locked: false, checksImplemented: false, unimplementedDomains: [] } });
    if (path === "/api/v1/company/branches") return this.json(route, 200, { branches: [{ id: "b1", code: "MAIN", nameAr: "الرئيسي", nameEn: "Main", isActive: true, isMain: true }], limit: null, everCreated: null, can: { create: false, activate: false, designate: false, update: [] } });
    if (path === "/api/v1/company/journal/cost-centers") return this.json(route, 200, { costCenters: [] });
    if (path === "/api/v1/company/journal" && method === "GET") {
      if (!can("company.journal.view")) return this.json(route, 403, { error: "forbidden" });
      return this.json(route, 200, { entries: this.entries.map((e) => e.entry), can: { create: can("company.journal.create"), approve: false, post: false, reverse: false, rateOverride: false, postInSoftClosed: false } });
    }
    const byId = /^\/api\/v1\/company\/journal\/(j\d+)$/.exec(path);
    if (byId && method === "GET") {
      const found = this.entries.find((e) => e.entry["id"] === byId[1]);
      if (!found) return this.json(route, 404, { error: "not_found" });
      return this.json(route, 200, { entry: found.entry, balanced: true, baseDebitTotal: "125.5000", baseCreditTotal: "125.5000", requiredApprovals: 1, lines: found.lines, approvals: [], events: [{ id: 1, kind: "Created", actor: "u1", atUtc: "2026-03-10T00:00:00Z", revision: 1, detail: null }] });
    }
    if (path === "/api/v1/company/journal" && method === "POST") {
      if (!can("company.journal.create")) return this.json(route, 403, { error: "forbidden" });
      const input = JSON.parse(request.postData() ?? "{}") as { lines?: Record<string, unknown>[]; entryDate?: string; description?: string | null; branchId?: string };
      const id = `j${this.entries.length + 1}`;
      const entry = { id, number: null, status: "draft", type: "normal", source: "manual", sourceKey: null, entryDate: input.entryDate ?? "2026-03-10", description: input.description ?? null, branchId: input.branchId ?? "b1", baseCurrency: "JOD", baseTotal: "125.5000", revision: 1, createdBy: "u1", createdAtUtc: "2026-03-10T00:00:00Z", updatedAtUtc: "2026-03-10T00:00:00Z", submittedBy: null, submittedAtUtc: null, postedBy: null, postedAtUtc: null, periodId: null, postedInSoftClosedPeriod: false, reversesId: null, adjustsId: null };
      const lines = (input.lines ?? []).map((l, i) => ({ id: `${id}l${i + 1}`, lineNo: i + 1, accountId: l["accountId"], description: null, currency: "JOD", debit: l["debit"] ?? "0.0000", credit: l["credit"] ?? "0.0000", rate: "1.0000000000", rateId: null, rateOverrideReason: null, baseDebit: l["debit"] ?? "0.0000", baseCredit: l["credit"] ?? "0.0000", branchId: "b1", isRounding: false, reversesLineId: null, allocations: [] }));
      this.entries.push({ entry, lines }); this.created.journal.push(input);
      return this.json(route, 201, { entry, balanced: true, baseDebitTotal: "125.5000", baseCreditTotal: "125.5000", requiredApprovals: 1, lines, approvals: [], events: [{ id: 1, kind: "Created", actor: "u1", atUtc: "2026-03-10T00:00:00Z", revision: 1, detail: null }] });
    }
    // Anything the flows do not need (dashboard widgets and the like) is answered 404 and listed in `unhandled`, so a test can show that no flow depends on an endpoint the mock does not implement.
    this.unhandled.push(`${method} ${path}`);
    return this.json(route, 404, { error: "not_found" });
  }
}
