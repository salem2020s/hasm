// @vitest-environment jsdom
// Storage/localStorage are not Node globals on Node 24 (CI), only on newer Node (local); jsdom provides
// them so the "no browser persistence" assertions run identically everywhere.
import { describe, expect, it, vi } from "vitest";
import { ApiError, createApiClient } from "./index";

const json = (body: unknown, init: ResponseInit = {}) => new Response(JSON.stringify(body), { headers: { "Content-Type": "application/json" }, ...init });
const call = (fetchMock: ReturnType<typeof vi.fn>, n = 0) => ({ url: fetchMock.mock.calls[n][0] as string, init: fetchMock.mock.calls[n][1] as RequestInit });

describe("api client", () => {
  it("lists party roles unchanged without a query and builds the optional search and paging parameters", async () => {
    const fetchMock = vi.fn().mockImplementation(() => Promise.resolve(json({ items: [], can: { configure: false } })));
    const client = createApiClient("https://x", fetchMock as unknown as typeof fetch);
    await client.listPartyRoles();
    await client.listPartyRoles(1);
    await client.listPartyRoles(undefined, { search: "a&b c", limit: 25, offset: 0 });
    await client.listPartyRoles(2, { search: "" });
    const B = "https://x/api/v1/company/party-roles";
    expect([0, 1, 2, 3].map((n) => call(fetchMock, n).url)).toEqual([B, B + "?kind=1", B + "?search=a%26b+c&limit=25&offset=0", B + "?kind=2"]);
  });

  it("looks party roles up by party ids with repeated ids parameters", async () => {
    const fetchMock = vi.fn().mockImplementation(() => Promise.resolve(json({ items: [], can: { configure: false }, total: 0 })));
    const client = createApiClient("https://x", fetchMock as unknown as typeof fetch);
    await client.listPartyRoles(1, { ids: ["p1", "p 2"] });
    expect(call(fetchMock, 0).url).toBe("https://x/api/v1/company/party-roles?kind=1&ids=p1&ids=p+2");
  });

  it("answers an empty party-id lookup with an empty page and NO request, so it can never fall back to the unfiltered list", async () => {
    const fetchMock = vi.fn().mockImplementation(() => Promise.resolve(json({ items: [{ id: "everything" }], can: { configure: true } })));
    const client = createApiClient("https://x", fetchMock as unknown as typeof fetch);
    const empty = await client.listPartyRoles(1, { ids: [] });
    expect(empty).toEqual({ items: [], total: 0 });
    expect(empty.can).toBeUndefined();                                               // no request, so no permission is known and none is invented
    expect(fetchMock).not.toHaveBeenCalled();
    await client.listPartyRoles(1, { search: "a" });                                   // other forms still call the server
    await client.listPartyRoles(undefined, {});                                        // ids absent is the unfiltered list
    expect(fetchMock).toHaveBeenCalledTimes(2);
    expect([0, 1].map((n) => call(fetchMock, n).url)).toEqual(["https://x/api/v1/company/party-roles?kind=1&search=a", "https://x/api/v1/company/party-roles"]);
  });

  it("lists units, item groups and customer groups unchanged without a query and with the optional search and paging", async () => {
    const fetchMock = vi.fn().mockImplementation(() => Promise.resolve(json({})));
    const client = createApiClient("https://x", fetchMock as unknown as typeof fetch);
    await client.listInventoryUnits();
    await client.listInventoryUnits({ search: "pc s", limit: 20, offset: 40 });
    await client.listInventoryGroups();
    await client.listInventoryGroups({ search: "a&b" });
    await client.listCustomerGroups();
    await client.listCustomerGroups({ limit: 5, offset: 0 });
    await client.listCustomerGroups({ active: true });
    await client.listCustomerGroups({ search: "v", active: false, limit: 10 });
    const I = "https://x/api/v1/company/inventory", P = "https://x/api/v1/company/pricing";
    expect([0, 1, 2, 3, 4, 5, 6, 7].map((n) => call(fetchMock, n).url)).toEqual([
      I + "/units", I + "/units?search=pc+s&limit=20&offset=40", I + "/groups", I + "/groups?search=a%26b", P + "/customer-groups", P + "/customer-groups?limit=5&offset=0",
      P + "/customer-groups?active=true", P + "/customer-groups?search=v&limit=10&active=false",
    ]);
  });

  it("requests the health endpoint under the base url", async () => {
    const fetchMock = vi.fn().mockResolvedValue(json({ status: "healthy", service: "hasm-api", version: "1.0.0" }));
    const client = createApiClient("https://localhost:7241/", fetchMock as unknown as typeof fetch);
    const health = await client.getHealth();
    expect(call(fetchMock).url).toBe("https://localhost:7241/api/v1/health");
    expect(health.status).toBe("healthy");
  });

  it("throws ApiError on non-success responses, carrying only the server's short code", async () => {
    const fetchMock = vi.fn().mockResolvedValue(new Response("", { status: 503 }));
    const client = createApiClient("https://x", fetchMock as unknown as typeof fetch);
    await expect(client.getHealth()).rejects.toBeInstanceOf(ApiError);
    fetchMock.mockResolvedValue(json({ error: "company_unavailable" }, { status: 503 }));
    await expect(client.getHealth()).rejects.toMatchObject({ status: 503, code: "company_unavailable" });
  });

  it("sends the HttpOnly session cookie (credentials) and no-store on every web call", async () => {
    const fetchMock = vi.fn().mockResolvedValue(json({ companies: [] }));
    await createApiClient("https://x", fetchMock as unknown as typeof fetch).listCompanies();
    expect(call(fetchMock).init.credentials).toBe("include");
    expect(call(fetchMock).init.cache).toBe("no-store");
  });

  it("adds the anti-CSRF header to state-changing calls only", async () => {
    const fetchMock = vi.fn().mockImplementation(() => Promise.resolve(json({})));
    const client = createApiClient("https://x", fetchMock as unknown as typeof fetch);
    await client.getProfile();
    await client.selectCompany("c1");
    await client.clearCompany().catch(() => undefined);
    expect((call(fetchMock, 0).init.headers as Record<string, string>)["X-Hasm-Request"]).toBeUndefined();
    expect((call(fetchMock, 1).init.headers as Record<string, string>)["X-Hasm-Request"]).toBe("1");
    expect((call(fetchMock, 2).init.headers as Record<string, string>)["X-Hasm-Request"]).toBe("1");
  });

  it("sends If-Match with the revision on updates and surfaces 412 with the current revision", async () => {
    const fetchMock = vi.fn().mockResolvedValue(json({ error: "revision_conflict", currentRevision: 7 }, { status: 412 }));
    const client = createApiClient("https://x", fetchMock as unknown as typeof fetch);
    await expect(client.putSettings({ defaultLanguage: "ar", dateFormat: "yyyy-MM-dd", numeralSystem: "latin", timeZone: null }, 3))
      .rejects.toMatchObject({ status: 412, code: "revision_conflict", details: { currentRevision: 7 } });
    expect((call(fetchMock).init.headers as Record<string, string>)["If-Match"]).toBe('"3"');
    expect(call(fetchMock).init.method).toBe("PUT");
  });

  it("maps validation field errors", async () => {
    const fetchMock = vi.fn().mockResolvedValue(json({ error: "validation_failed", fields: [{ field: "email", code: "invalid_format" }] }, { status: 400 }));
    const client = createApiClient("https://x", fetchMock as unknown as typeof fetch);
    await expect(client.getProfile()).rejects.toMatchObject({ details: { fields: [{ field: "email", code: "invalid_format" }] } });
  });

  it("notifies when an authenticated call is answered 401, but not for the login or status calls", async () => {
    const onUnauthenticated = vi.fn();
    const fetchMock = vi.fn().mockResolvedValue(json({ error: "unauthenticated" }, { status: 401 }));
    const client = createApiClient("https://x", fetchMock as unknown as typeof fetch, { onUnauthenticated });
    await expect(client.getProfile()).rejects.toBeInstanceOf(ApiError);
    expect(onUnauthenticated).toHaveBeenCalledTimes(1);
    await expect(client.login("a", "b")).rejects.toBeInstanceOf(ApiError);
    await expect(client.status()).rejects.toBeInstanceOf(ApiError);
    expect(onUnauthenticated).toHaveBeenCalledTimes(1);
  });

  it("carries the password-policy problems of a weak_password refusal (strings only)", async () => {
    const fetchMock = vi.fn().mockResolvedValue(json({ error: "weak_password", problems: ["too_short", 5, "too_repetitive"] }, { status: 400 }));
    const client = createApiClient("https://x", fetchMock as unknown as typeof fetch);
    await expect(client.login("a", "b")).rejects.toMatchObject({ status: 400, code: "weak_password", details: { problems: ["too_short", "too_repetitive"] } });
  });

  it("reports Retry-After for throttled logins", async () => {
    const fetchMock = vi.fn().mockResolvedValue(json({ error: "too_many_attempts" }, { status: 429, headers: { "Retry-After": "42" } }));
    const client = createApiClient("https://x", fetchMock as unknown as typeof fetch);
    await expect(client.login("a", "b")).rejects.toMatchObject({ status: 429, details: { retryAfterSeconds: 42 } });
  });

  it("desktop mode keeps the bearer token in memory only and never sends cookies", async () => {
    const setItem = vi.spyOn(Storage.prototype, "setItem");
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(json({ user: { id: "u", login: "l", displayName: "d", isIdentityAdministrator: false }, token: "opaque-token" }))
      .mockImplementation(() => Promise.resolve(json({ companies: [] })));
    const client = createApiClient("https://x", fetchMock as unknown as typeof fetch, { client: "desktop" });
    await client.login("l", "p");
    expect(JSON.parse(call(fetchMock).init.body as string)).toMatchObject({ client: "desktop" });
    await client.listCompanies();
    const second = call(fetchMock, 1).init;
    expect((second.headers as Record<string, string>)["Authorization"]).toBe("Bearer opaque-token");
    expect(second.credentials).toBe("omit");
    expect(setItem).not.toHaveBeenCalled();
    expect(localStorage.length).toBe(0);
    expect(sessionStorage.length).toBe(0);
    expect(document.cookie).toBe("");
    await client.logout().catch(() => undefined);
    await client.listCompanies();
    expect((call(fetchMock, 3).init.headers as Record<string, string>)["Authorization"]).toBeUndefined();
  });

  it("web mode asks for a cookie session and never reads a token", async () => {
    const fetchMock = vi.fn().mockResolvedValue(json({ user: { id: "u", login: "l", displayName: "d", isIdentityAdministrator: false }, token: null }));
    await createApiClient("https://x", fetchMock as unknown as typeof fetch).login("l", "p");
    expect(JSON.parse(call(fetchMock).init.body as string)).toMatchObject({ client: "web" });
  });
});

describe("api client: branches (same endpoints for Web and Desktop)", () => {
  const branch = { id: "b1", code: "MAIN", revision: 1 };
  const headersOf = (fetchMock: ReturnType<typeof vi.fn>, n = 0) => call(fetchMock, n).init.headers as Record<string, string>;

  it("lists, reads and creates branches with the anti-CSRF header on the state change only", async () => {
    const fetchMock = vi.fn().mockImplementation(() => Promise.resolve(json(branch)));
    const client = createApiClient("https://x", fetchMock as unknown as typeof fetch);
    await client.listBranches();
    await client.getBranch("a b");
    await client.createBranch({ code: "N1", nameAr: null, nameEn: "North", addressLine1: null, addressLine2: null, city: null, region: null, postalCode: null, phone: null, email: null, website: null });
    expect(call(fetchMock, 0).url).toBe("https://x/api/v1/company/branches");
    expect(call(fetchMock, 1).url).toBe("https://x/api/v1/company/branches/a%20b");
    expect(call(fetchMock, 2)).toMatchObject({ url: "https://x/api/v1/company/branches", init: { method: "POST" } });
    expect(JSON.parse(call(fetchMock, 2).init.body as string)).toMatchObject({ code: "N1", nameEn: "North" });
    expect(headersOf(fetchMock, 0)["X-Hasm-Request"]).toBeUndefined();
    expect(headersOf(fetchMock, 2)["X-Hasm-Request"]).toBe("1");
  });

  it("sends the branch revision as If-Match on update, activation, deactivation and main designation", async () => {
    const fetchMock = vi.fn().mockImplementation(() => Promise.resolve(json(branch)));
    const client = createApiClient("https://x", fetchMock as unknown as typeof fetch);
    await client.updateBranch("b1", { nameAr: "ا", nameEn: null, addressLine1: null, addressLine2: null, city: null, region: null, postalCode: null, phone: null, email: null, website: null }, 4);
    await client.deactivateBranch("b1", 5, "b2");
    await client.deactivateBranch("b1", 6);
    await client.activateBranch("b1", 7);
    await client.makeMainBranch("b1", 8);
    const urls = [0, 1, 2, 3, 4].map((n) => call(fetchMock, n).url.replace("https://x/api/v1/company/branches/", ""));
    expect(urls).toEqual(["b1", "b1/deactivate", "b1/deactivate", "b1/activate", "b1/make-main"]);
    expect([0, 1, 2, 3, 4].map((n) => headersOf(fetchMock, n)["If-Match"])).toEqual(['"4"', '"5"', '"6"', '"7"', '"8"']);
    expect(JSON.parse(call(fetchMock, 1).init.body as string)).toEqual({ newMainId: "b2" });
    expect(JSON.parse(call(fetchMock, 2).init.body as string)).toEqual({});
    expect(call(fetchMock, 0).init.method).toBe("PUT");
  });

  it("uses the bearer token and no cookies on Desktop for the same branch endpoints", async () => {
    const fetchMock = vi.fn().mockImplementation((url: string) => Promise.resolve(json(url.endsWith("/auth/login") ? { user: { id: "u" }, token: "tok" } : { branches: [] })));
    const client = createApiClient("https://x", fetchMock as unknown as typeof fetch, { client: "desktop" });
    await client.login("a", "b");
    await client.listBranches();
    const init = call(fetchMock, 1).init;
    expect((init.headers as Record<string, string>)["Authorization"]).toBe("Bearer tok");
    expect(init.credentials).toBe("omit");
    expect(call(fetchMock, 1).url).toBe("https://x/api/v1/company/branches");
  });

  it("surfaces the server's short code and current revision for branch conflicts", async () => {
    const fetchMock = vi.fn().mockResolvedValue(json({ error: "branch_code_taken" }, { status: 409 }));
    const client = createApiClient("https://x", fetchMock as unknown as typeof fetch);
    await expect(client.createBranch({ code: "X", nameAr: null, nameEn: "x", addressLine1: null, addressLine2: null, city: null, region: null, postalCode: null, phone: null, email: null, website: null }))
      .rejects.toMatchObject({ status: 409, code: "branch_code_taken" });
  });
});

describe("api client: warehouses (same endpoints for Web and Desktop)", () => {
  const warehouse = { id: "w1", code: "WH1", revision: 1 };
  const headersOf = (fetchMock: ReturnType<typeof vi.fn>, n = 0) => call(fetchMock, n).init.headers as Record<string, string>;
  const input = { nameAr: null, nameEn: "North", addressLine1: null, addressLine2: null, city: null, region: null, postalCode: null, phone: null, email: null, website: null };

  it("lists, reads and creates warehouses with the anti-CSRF header on the state change only", async () => {
    const fetchMock = vi.fn().mockImplementation(() => Promise.resolve(json(warehouse)));
    const client = createApiClient("https://x", fetchMock as unknown as typeof fetch);
    await client.listWarehouses();
    await client.getWarehouse("a b");
    await client.createWarehouse({ ...input, code: "N1", branchId: "b1" });
    expect(call(fetchMock, 0).url).toBe("https://x/api/v1/company/warehouses");
    expect(call(fetchMock, 1).url).toBe("https://x/api/v1/company/warehouses/a%20b");
    expect(call(fetchMock, 2)).toMatchObject({ url: "https://x/api/v1/company/warehouses", init: { method: "POST" } });
    expect(JSON.parse(call(fetchMock, 2).init.body as string)).toMatchObject({ code: "N1", branchId: "b1", nameEn: "North" });
    expect(headersOf(fetchMock, 0)["X-Hasm-Request"]).toBeUndefined();
    expect(headersOf(fetchMock, 2)["X-Hasm-Request"]).toBe("1");
  });

  it("sends the warehouse revision as If-Match on update, activation and deactivation", async () => {
    const fetchMock = vi.fn().mockImplementation(() => Promise.resolve(json(warehouse)));
    const client = createApiClient("https://x", fetchMock as unknown as typeof fetch);
    await client.updateWarehouse("w1", input, 4);
    await client.deactivateWarehouse("w1", 5);
    await client.activateWarehouse("w1", 6);
    const urls = [0, 1, 2].map((n) => call(fetchMock, n).url.replace("https://x/api/v1/company/warehouses/", ""));
    expect(urls).toEqual(["w1", "w1/deactivate", "w1/activate"]);
    expect([0, 1, 2].map((n) => headersOf(fetchMock, n)["If-Match"])).toEqual(['"4"', '"5"', '"6"']);
    expect(call(fetchMock, 0).init.method).toBe("PUT");
  });

  it("calls the inventory master-data endpoints with the revision as If-Match, encoded ids and query filters (Web)", async () => {
    const fetchMock = vi.fn().mockImplementation(() => Promise.resolve(json({})));
    const client = createApiClient("https://x", fetchMock as unknown as typeof fetch);
    const B = "https://x/api/v1/company/inventory";
    await client.getInventoryCapabilities();
    await client.putInventorySettings(2, 0);
    await client.updateInventoryUnit("u 1", { nameAr: null, nameEn: "Piece", decimals: 0 }, 3);
    await client.deactivateInventoryGroup("g1", 4);
    await client.listInventoryItems({ search: "a&b", groupId: "g1", active: true, type: 2, offset: 25, limit: 25 });
    await client.updateInventoryItem("i1", { code: null, nameAr: "x", nameEn: null, itemType: 1, groupId: "g", baseUnitId: "u", purchaseUnitId: null, salesUnitId: null, isLot: false, isSerial: false, hasExpiry: false, notes: null, alternateUnits: [] }, 7);
    await client.addItemBarcode("i1", { barcode: "123" });
    await client.deactivateItemBarcode("i1", "b1");
    await client.lookupBarcode("A B");
    expect([0, 1, 2, 3, 4, 5, 6, 7, 8].map((n) => call(fetchMock, n).url)).toEqual([
      B + "/capabilities", B + "/settings", B + "/units/u%201", B + "/groups/g1/deactivate", B + "/items?search=a%26b&groupId=g1&active=true&type=2&offset=25&limit=25", B + "/items/i1",
      B + "/items/i1/barcodes", B + "/items/i1/barcodes/b1/deactivate", B + "/barcode-lookup?barcode=A%20B",
    ]);
    expect([1, 2, 3, 5].map((n) => headersOf(fetchMock, n)["If-Match"])).toEqual(['"0"', '"3"', '"4"', '"7"']);
    expect([0, 4, 8].map((n) => headersOf(fetchMock, n)["X-Hasm-Request"])).toEqual([undefined, undefined, undefined]);              // reads carry no anti-CSRF header
    expect([1, 2, 3, 5, 6, 7].every((n) => headersOf(fetchMock, n)["X-Hasm-Request"] === "1")).toBe(true);                         // every change does
    expect(JSON.parse(call(fetchMock, 1).init.body as string)).toEqual({ defaultValuation: 2 });
  });

  it("uses the bearer token and no cookies on Desktop for the same warehouse endpoints", async () => {
    const fetchMock = vi.fn().mockImplementation((url: string) => Promise.resolve(json(url.endsWith("/auth/login") ? { user: { id: "u" }, token: "tok" } : { warehouses: [] })));
    const client = createApiClient("https://x", fetchMock as unknown as typeof fetch, { client: "desktop" });
    await client.login("a", "b");
    await client.listWarehouses();
    const init = call(fetchMock, 1).init;
    expect((init.headers as Record<string, string>)["Authorization"]).toBe("Bearer tok");
    expect(init.credentials).toBe("omit");
    expect(call(fetchMock, 1).url).toBe("https://x/api/v1/company/warehouses");
  });

  it("surfaces the server's short code for warehouse refusals", async () => {
    const fetchMock = vi.fn().mockResolvedValue(json({ error: "branch_inactive" }, { status: 409 }));
    const client = createApiClient("https://x", fetchMock as unknown as typeof fetch);
    await expect(client.createWarehouse({ ...input, code: "X", branchId: "b" })).rejects.toMatchObject({ status: 409, code: "branch_inactive" });
  });
});

describe("api client: fiscal calendar (same endpoints for Web and Desktop)", () => {
  const headersOf = (fetchMock: ReturnType<typeof vi.fn>, n = 0) => call(fetchMock, n).init.headers as Record<string, string>;

  it("lists, reads and creates years with the anti-CSRF header on the state change only", async () => {
    const fetchMock = vi.fn().mockImplementation(() => Promise.resolve(json({ id: "y1", revision: 1 })));
    const client = createApiClient("https://x", fetchMock as unknown as typeof fetch);
    await client.listFiscalYears();
    await client.getFiscalYear("a b");
    await client.createFiscalYear({ name: "FY2026", startDate: "2026-01-01", periodType: "monthly" });
    expect(call(fetchMock, 0).url).toBe("https://x/api/v1/company/fiscal-years");
    expect(call(fetchMock, 1).url).toBe("https://x/api/v1/company/fiscal-years/a%20b");
    expect(JSON.parse(call(fetchMock, 2).init.body as string)).toEqual({ name: "FY2026", startDate: "2026-01-01", periodType: "monthly" });
    expect(headersOf(fetchMock, 0)["X-Hasm-Request"]).toBeUndefined();
    expect(headersOf(fetchMock, 2)["X-Hasm-Request"]).toBe("1");
  });

  it("sends the revision as If-Match on every closing and routine reopening", async () => {
    const fetchMock = vi.fn().mockImplementation(() => Promise.resolve(json({ id: "x", revision: 1 })));
    const client = createApiClient("https://x", fetchMock as unknown as typeof fetch);
    await client.softCloseFiscalPeriod("p1", 1);
    await client.reopenFiscalPeriod("p1", 2);
    await client.finalCloseFiscalPeriod("p1", 3);
    await client.closeFiscalYear("y1", 4);
    const paths = [0, 1, 2, 3].map((n) => call(fetchMock, n).url.replace("https://x/api/v1/company/", ""));
    expect(paths).toEqual(["fiscal-periods/p1/soft-close", "fiscal-periods/p1/reopen", "fiscal-periods/p1/final-close", "fiscal-years/y1/close"]);
    expect([0, 1, 2, 3].map((n) => headersOf(fetchMock, n)["If-Match"])).toEqual(['"1"', '"2"', '"3"', '"4"']);
  });

  it("has no one-step exceptional reopening: only a request, then a decision by someone else", async () => {
    const fetchMock = vi.fn().mockImplementation(() => Promise.resolve(json({ id: "r1", revision: 1 })));
    const client = createApiClient("https://x", fetchMock as unknown as typeof fetch) as unknown as Record<string, unknown>;
    expect(client.reopenFinalFiscalPeriod).toBeUndefined();
    expect(client.reopenFiscalYear).toBeUndefined();
    const api = createApiClient("https://x", fetchMock as unknown as typeof fetch);
    await api.requestFiscalPeriodReopen("p1", 3, "A documented reason");
    await api.requestFiscalYearReopen("y1", 4, "Another documented reason");
    await api.listFiscalReopenRequests();
    await api.approveFiscalReopen("r1", 1);
    await api.denyFiscalReopen("r1", 2, "Not justified");
    await api.denyFiscalReopen("r1", 3);
    await api.cancelFiscalReopen("r1", 4);
    const paths = [0, 1, 2, 3, 4, 5, 6].map((n) => call(fetchMock, n).url.replace("https://x/api/v1/company/", ""));
    expect(paths).toEqual(["fiscal-periods/p1/reopen-requests", "fiscal-years/y1/reopen-requests", "fiscal-reopen-requests", "fiscal-reopen-requests/r1/approve",
      "fiscal-reopen-requests/r1/deny", "fiscal-reopen-requests/r1/deny", "fiscal-reopen-requests/r1/cancel"]);
    expect([0, 1, 3, 4, 5, 6].map((n) => headersOf(fetchMock, n)["If-Match"])).toEqual(['"3"', '"4"', '"1"', '"2"', '"3"', '"4"']);
    expect(JSON.parse(call(fetchMock, 0).init.body as string)).toEqual({ reason: "A documented reason" });
    expect(JSON.parse(call(fetchMock, 4).init.body as string)).toEqual({ note: "Not justified" });
    expect(JSON.parse(call(fetchMock, 5).init.body as string)).toEqual({});
    expect(headersOf(fetchMock, 2)["X-Hasm-Request"]).toBeUndefined();
    expect(headersOf(fetchMock, 3)["X-Hasm-Request"]).toBe("1");
  });


  it("uses the bearer token and no cookies on Desktop for the same fiscal endpoints", async () => {
    const fetchMock = vi.fn().mockImplementation((url: string) => Promise.resolve(json(url.endsWith("/auth/login") ? { user: { id: "u" }, token: "tok" } : { years: [] })));
    const client = createApiClient("https://x", fetchMock as unknown as typeof fetch, { client: "desktop" });
    await client.login("a", "b");
    await client.listFiscalYears();
    const init = call(fetchMock, 1).init;
    expect((init.headers as Record<string, string>)["Authorization"]).toBe("Bearer tok");
    expect(init.credentials).toBe("omit");
    expect(call(fetchMock, 1).url).toBe("https://x/api/v1/company/fiscal-years");
  });

  it("surfaces the server's short code for fiscal refusals", async () => {
    const fetchMock = vi.fn().mockResolvedValue(json({ error: "segregation_of_duties" }, { status: 403 }));
    const client = createApiClient("https://x", fetchMock as unknown as typeof fetch);
    await expect(client.approveFiscalReopen("r1", 1)).rejects.toMatchObject({ status: 403, code: "segregation_of_duties" });
  });
});

describe("api client: chart of accounts (same endpoints for Web and Desktop)", () => {
  const headersOf = (fetchMock: ReturnType<typeof vi.fn>, n = 0) => call(fetchMock, n).init.headers as Record<string, string>;
  const account = { id: "a1", code: "1", revision: 1 };

  it("lists, reads and creates accounts with the anti-CSRF header on the state change only", async () => {
    const fetchMock = vi.fn().mockImplementation(() => Promise.resolve(json(account)));
    const client = createApiClient("https://x", fetchMock as unknown as typeof fetch);
    await client.listAccounts();
    await client.getAccount("a b");
    await client.createAccount({ code: null, parentId: "p1", nameAr: null, nameEn: "Cash", isGroup: false });
    expect(call(fetchMock, 0).url).toBe("https://x/api/v1/company/accounts");
    expect(call(fetchMock, 1).url).toBe("https://x/api/v1/company/accounts/a%20b");
    expect(JSON.parse(call(fetchMock, 2).init.body as string)).toMatchObject({ code: null, parentId: "p1", nameEn: "Cash", isGroup: false });
    expect(headersOf(fetchMock, 0)["X-Hasm-Request"]).toBeUndefined();
    expect(headersOf(fetchMock, 2)["X-Hasm-Request"]).toBe("1");
  });

  it("sends the revision as If-Match on update, re-coding, moving, activation and the settings, and the template without one", async () => {
    const fetchMock = vi.fn().mockImplementation(() => Promise.resolve(json(account)));
    const client = createApiClient("https://x", fetchMock as unknown as typeof fetch);
    await client.updateAccount("a1", { nameAr: "ا", nameEn: null, nature: "debit", isGroup: false, currencyPolicy: "base", currencyCode: null }, 2);
    await client.recodeAccount("a1", "2001", 3);
    await client.moveAccount("a1", null, 4);
    await client.deactivateAccount("a1", 5);
    await client.activateAccount("a1", 6);
    await client.putChartSettings({ segmentWidth: 3, autoCodes: false }, 0);
    await client.applyChartTemplate("generic");
    const paths = [0, 1, 2, 3, 4, 5, 6].map((n) => call(fetchMock, n).url.replace("https://x/api/v1/company/accounts", "") || "/");
    expect(paths).toEqual(["/a1", "/a1/recode", "/a1/move", "/a1/deactivate", "/a1/activate", "/settings", "/apply-template"]);
    expect([0, 1, 2, 3, 4, 5].map((n) => headersOf(fetchMock, n)["If-Match"])).toEqual(['"2"', '"3"', '"4"', '"5"', '"6"', '"0"']);
    expect(headersOf(fetchMock, 6)["If-Match"]).toBeUndefined();
    expect(JSON.parse(call(fetchMock, 1).init.body as string)).toEqual({ code: "2001" });
    expect(JSON.parse(call(fetchMock, 2).init.body as string)).toEqual({ parentId: null });
    expect(JSON.parse(call(fetchMock, 6).init.body as string)).toEqual({ template: "generic" });
  });

  it("uses the bearer token and no cookies on Desktop for the same chart endpoints", async () => {
    const fetchMock = vi.fn().mockImplementation((url: string) => Promise.resolve(json(url.endsWith("/auth/login") ? { user: { id: "u" }, token: "tok" } : { accounts: [] })));
    const client = createApiClient("https://x", fetchMock as unknown as typeof fetch, { client: "desktop" });
    await client.login("a", "b");
    await client.listAccounts();
    const init = call(fetchMock, 1).init;
    expect((init.headers as Record<string, string>)["Authorization"]).toBe("Bearer tok");
    expect(init.credentials).toBe("omit");
    expect(call(fetchMock, 1).url).toBe("https://x/api/v1/company/accounts");
  });

  it("surfaces the server's short code for chart refusals", async () => {
    const fetchMock = vi.fn().mockResolvedValue(json({ error: "account_has_postings" }, { status: 409 }));
    const client = createApiClient("https://x", fetchMock as unknown as typeof fetch);
    await expect(client.updateAccount("a1", { nameAr: "ا", nameEn: null, nature: "credit", isGroup: false, currencyPolicy: "base", currencyCode: null }, 1))
      .rejects.toMatchObject({ status: 409, code: "account_has_postings" });
  });
});

describe("ledger endpoints (Phase 3.2B)", () => {
  const make = () => {
    const fetchMock = vi.fn().mockImplementation(() => Promise.resolve(json({})));
    return { fetchMock, client: createApiClient("https://x", fetchMock as unknown as typeof fetch) };
  };
  const h = (init: RequestInit) => init.headers as Record<string, string>;

  it("sends the exchange-rate entry with the currency's latest id as If-Match and the exact decimal text", async () => {
    const { fetchMock, client } = make();
    await client.enterExchangeRate({ currencyCode: "USD", rate: "0.7100000000", validFrom: "2026-01-01", note: null }, 7);
    const { url, init } = call(fetchMock);
    expect(url).toBe("https://x/api/v1/company/exchange-rates");
    expect(h(init)["If-Match"]).toBe('"7"');
    expect(h(init)["X-Hasm-Request"]).toBe("1");
    expect(JSON.parse(init.body as string)).toEqual({ currencyCode: "USD", rate: "0.7100000000", validFrom: "2026-01-01", note: null });
  });

  it("sends every journal workflow step with the entry revision and keeps amounts as text", async () => {
    const { fetchMock, client } = make();
    await client.createJournalEntry({ entryDate: "2026-03-10", branchId: "b", lines: [{ accountId: "a", debit: "100.125" }, { accountId: "c", credit: "100.125" }] });
    await client.updateJournalEntry("j1", { entryDate: "2026-03-10", branchId: "b", lines: [] }, 3);
    await client.submitJournalEntry("j1", 3);
    await client.decideJournalEntry("j1", false, "no", 3);
    await client.postJournalEntry("j1", 3);
    await client.reverseJournalEntry("j1", "2026-04-01", null, 3);
    expect(JSON.parse(call(fetchMock, 0).init.body as string).lines[0].debit).toBe("100.125");
    const urls = [1, 2, 3, 4, 5].map((i) => call(fetchMock, i).url);
    expect(urls).toEqual(["https://x/api/v1/company/journal/j1", "https://x/api/v1/company/journal/j1/submit", "https://x/api/v1/company/journal/j1/decision", "https://x/api/v1/company/journal/j1/post", "https://x/api/v1/company/journal/j1/reverse"]);
    for (const i of [1, 2, 3, 4, 5]) expect(h(call(fetchMock, i).init)["If-Match"]).toBe('"3"');
    expect(JSON.parse(call(fetchMock, 3).init.body as string)).toEqual({ approve: false, comment: "no" });
  });

  it("builds the list and report queries with encoded parameters", async () => {
    const { fetchMock, client } = make();
    await client.listJournal({ status: "posted", from: "2026-01-01", text: "a b" });
    await client.getTrialBalance({ from: "2026-01-01", to: "2026-12-31", branchId: "b1", includeZero: true });
    await client.getGeneralLedger("a 1", { from: "2026-01-01", to: "2026-12-31", costCenterId: "c1" }, 200);
    expect(call(fetchMock, 0).url).toBe("https://x/api/v1/company/journal?status=posted&from=2026-01-01&q=a+b");
    expect(call(fetchMock, 1).url).toBe("https://x/api/v1/company/reports/trial-balance?from=2026-01-01&to=2026-12-31&branchId=b1&includeZero=true");
    expect(call(fetchMock, 2).url).toBe("https://x/api/v1/company/reports/general-ledger?accountId=a%201&from=2026-01-01&to=2026-12-31&costCenterId=c1&limit=200&offset=200");
  });

  it("downloads a CSV as a blob over the same transport rules and reports a failure with the server code", async () => {
    const fetchMock = vi.fn().mockResolvedValueOnce(new Response("a,b", { headers: { "Content-Type": "text/csv" } })).mockResolvedValueOnce(json({ error: "forbidden" }, { status: 403 }));
    const client = createApiClient("https://x", fetchMock as unknown as typeof fetch);
    const blob = await client.downloadTrialBalanceCsv({ from: "2026-01-01", to: "2026-12-31" });
    expect(await blob.text()).toBe("a,b");
    expect(call(fetchMock, 0).url).toBe("https://x/api/v1/company/reports/trial-balance?from=2026-01-01&to=2026-12-31&format=csv");
    expect(call(fetchMock, 0).init.credentials).toBe("include");
    await expect(client.downloadGeneralLedgerCsv("a1", { from: "2026-01-01", to: "2026-12-31" })).rejects.toMatchObject({ status: 403, code: "forbidden" });
  });

  it("sends the desktop bearer token on a download and never uses cookies there", async () => {
    const fetchMock = vi.fn().mockResolvedValueOnce(json({ user: { id: "u" }, token: "tok" })).mockResolvedValueOnce(new Response("x"));
    const client = createApiClient("https://x", fetchMock as unknown as typeof fetch, { client: "desktop" });
    await client.login("amal", "pw");
    await client.downloadTrialBalanceCsv({ from: "2026-01-01", to: "2026-12-31" });
    const { init } = call(fetchMock, 1);
    expect(h(init)["Authorization"]).toBe("Bearer tok");
    expect(init.credentials).toBe("omit");
  });

  it("manages numbering, cost centers and approval policies with revision control", async () => {
    const { fetchMock, client } = make();
    await client.putNumberingRule("journal_entry", { prefix: "JV", separator: "-", width: 6, perBranch: false, perFiscalYear: true }, 2);
    await client.moveCostCenter("c1", null, 4);
    await client.updateApprovalPolicy("p1", { source: "any", minAmount: "10", requiredApprovals: 2, nameAr: null, nameEn: "x" }, 5);
    expect([0, 1, 2].map((i) => h(call(fetchMock, i).init)["If-Match"])).toEqual(['"2"', '"4"', '"5"']);
    expect(call(fetchMock, 1).url).toBe("https://x/api/v1/company/cost-centers/c1/move");
  });

  it("calls the cost-center policy endpoints with the rule revision (0 = never configured) and the journal lookup", async () => {
    const { fetchMock, client } = make();
    await client.setAccountCostCenterRule("a1", true, 0);
    await client.setCostCenterBranchRule("c1", { restricted: true, branchIds: ["b1"] }, 3);
    await client.listAllocatableCostCenters();
    expect(call(fetchMock, 0).url).toBe("https://x/api/v1/company/cost-center-policies/accounts/a1");
    expect(h(call(fetchMock, 0).init)["If-Match"]).toBe('"0"');
    expect(JSON.parse(call(fetchMock, 0).init.body as string)).toEqual({ required: true });
    expect(call(fetchMock, 1).url).toBe("https://x/api/v1/company/cost-center-policies/branches/c1");
    expect(h(call(fetchMock, 1).init)["If-Match"]).toBe('"3"');
    expect(JSON.parse(call(fetchMock, 1).init.body as string)).toEqual({ restricted: true, branchIds: ["b1"] });
    expect(call(fetchMock, 2).url).toBe("https://x/api/v1/company/journal/cost-centers");
  });

  it("calls the year-end closing endpoints: settings with the revision, preview, close, carry forward and the two reversals", async () => {
    const { fetchMock, client } = make();
    await client.putClosingSettings({ equityAccountId: "a1" }, 2);
    await client.previewYearEnd("y1");
    await client.startYearClosing("y1");
    await client.carryForwardYear("y1");
    await client.reverseYearOpening("y1", "2027-01-01");
    await client.reverseYearClosing("y1");
    const urls = [0, 1, 2, 3, 4, 5].map((i) => call(fetchMock, i).url);
    expect(urls).toEqual([
      "https://x/api/v1/company/fiscal-closing/settings", "https://x/api/v1/company/fiscal-closing/years/y1/preview", "https://x/api/v1/company/fiscal-closing/years/y1/close",
      "https://x/api/v1/company/fiscal-closing/years/y1/carry-forward", "https://x/api/v1/company/fiscal-closing/years/y1/reverse-opening", "https://x/api/v1/company/fiscal-closing/years/y1/reverse-closing",
    ]);
    expect(h(call(fetchMock, 0).init)["If-Match"]).toBe('"2"');
    expect(JSON.parse(call(fetchMock, 4).init.body as string)).toEqual({ entryDate: "2027-01-01" });
  });

  it("sends staged approval policies with their risk conditions and the exclude-closing report flag", async () => {
    const { fetchMock, client } = make();
    await client.createApprovalPolicy({ docType: "journal_entry", source: "any", minAmount: "0", nameAr: null, nameEn: "x", journalType: "reversal", rateOverride: true, closingKind: "closing", stages: [{ required: 1 }, { required: 2 }] });
    await client.getTrialBalance({ from: "2026-01-01", to: "2026-12-31", excludeClosing: true });
    expect(JSON.parse(call(fetchMock, 0).init.body as string)).toMatchObject({ journalType: "reversal", rateOverride: true, closingKind: "closing", stages: [{ required: 1 }, { required: 2 }] });
    expect(call(fetchMock, 1).url).toContain("excludeClosing=true");
  });
});

describe("api client: import template routes (odd names)", () => {
  it("encodes / % ? # and spaces in template names and adapter codes for retire", async () => {
    const fetchMock = vi.fn().mockImplementation(() => Promise.resolve(json({ id: "t" })));
    const client = createApiClient("https://x", fetchMock as unknown as typeof fetch);
    for (const name of ["a/b", "50%", "why?", "tag#1", "two words"]) await client.retireImportTemplate("chart/of?accounts", name, 3);
    const urls = fetchMock.mock.calls.map((c) => c[0] as string);
    expect(urls).toEqual([
      "https://x/api/v1/company/imports/templates/chart%2Fof%3Faccounts/a%2Fb/retire",
      "https://x/api/v1/company/imports/templates/chart%2Fof%3Faccounts/50%25/retire",
      "https://x/api/v1/company/imports/templates/chart%2Fof%3Faccounts/why%3F/retire",
      "https://x/api/v1/company/imports/templates/chart%2Fof%3Faccounts/tag%231/retire",
      "https://x/api/v1/company/imports/templates/chart%2Fof%3Faccounts/two%20words/retire",
    ]);
    for (const u of urls) { const parsed = new URL(u); expect(parsed.search).toBe(""); expect(parsed.hash).toBe(""); expect(parsed.pathname.split("/").length).toBe(9); }
    expect(JSON.parse(fetchMock.mock.calls[0]![1].body as string)).toEqual({ expectedVersion: 3 });
  });
});

describe("api client: by-ids lookup of units and item groups", () => {
  const unitRow = (id: string) => ({ id, code: id.toUpperCase() });
  const idsOf = (url: string) => new URL(url).searchParams.getAll("ids");

  it("sends repeated ids on the list call and nothing else changes", async () => {
    const fetchMock = vi.fn().mockImplementation(() => Promise.resolve(json({ units: [], groups: [], total: 0 })));
    const client = createApiClient("https://x", fetchMock as unknown as typeof fetch);
    await client.listInventoryUnits({ ids: ["a", "b"], limit: 5 });
    await client.listInventoryGroups({ ids: ["c"] });
    await client.listInventoryUnits();
    expect(call(fetchMock, 0).url).toBe("https://x/api/v1/company/inventory/units?limit=5&ids=a&ids=b");
    expect(call(fetchMock, 1).url).toBe("https://x/api/v1/company/inventory/groups?ids=c");
    expect(call(fetchMock, 2).url).toBe("https://x/api/v1/company/inventory/units");
  });

  it("answers an explicit empty ids array locally with an empty page and sends NO request, for units and for groups (it must never become the unfiltered list)", async () => {
    const fetchMock = vi.fn();
    const client = createApiClient("https://x", fetchMock as unknown as typeof fetch);
    expect(await client.listInventoryUnits({ ids: [] })).toEqual({ units: [], total: 0 });
    expect(await client.listInventoryUnits({ ids: [], search: "a", limit: 5 })).toEqual({ units: [], total: 0 });
    expect(await client.listInventoryGroups({ ids: [] })).toEqual({ groups: [], total: 0 });
    expect(fetchMock).not.toHaveBeenCalled();
    // an ABSENT ids is still the ordinary list
    const ok = vi.fn().mockImplementation(() => Promise.resolve(json({ units: [], groups: [] })));
    const live = createApiClient("https://x", ok as unknown as typeof fetch);
    await live.listInventoryUnits({});
    await live.listInventoryGroups();
    expect(ok).toHaveBeenCalledTimes(2);
  });

  it("never sends a request for an empty id list (an empty ids would mean everything over HTTP)", async () => {
    const fetchMock = vi.fn();
    const client = createApiClient("https://x", fetchMock as unknown as typeof fetch);
    expect(await client.lookupInventoryUnits([])).toEqual([]);
    expect(await client.lookupInventoryGroups([])).toEqual([]);
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("removes duplicates, batches at most 100 ids per request and merges the rows", async () => {
    const fetchMock = vi.fn().mockImplementation((url: string) => Promise.resolve(json({ units: idsOf(url).map(unitRow) })));
    const client = createApiClient("https://x", fetchMock as unknown as typeof fetch);
    const ids = Array.from({ length: 150 }, (_, n) => `id-${n}`);
    const rows = await client.lookupInventoryUnits([...ids, ids[0]!, ids[1]!]);
    expect(fetchMock).toHaveBeenCalledTimes(2);
    expect(idsOf(call(fetchMock, 0).url)).toHaveLength(100);
    expect(idsOf(call(fetchMock, 1).url)).toHaveLength(50);
    expect(rows.map((r) => r.id)).toEqual(ids);                                       // every id once, in batch order
  });

  it("uses the same endpoint, credentials and no state-changing header for groups, and surfaces the server's refusal", async () => {
    const fetchMock = vi.fn().mockImplementationOnce(() => Promise.resolve(json({ groups: [{ id: "g1" }] }))).mockImplementationOnce(() => Promise.resolve(json({ error: "forbidden" }, { status: 403 })));
    const client = createApiClient("https://x", fetchMock as unknown as typeof fetch);
    expect(await client.lookupInventoryGroups(["g1"])).toEqual([{ id: "g1" }]);
    expect(call(fetchMock, 0).url).toBe("https://x/api/v1/company/inventory/groups?ids=g1");
    expect((call(fetchMock, 0).init.headers as Record<string, string>)["X-Hasm-Request"]).toBeUndefined();      // a read, not a state change
    await expect(client.lookupInventoryGroups(["g2"])).rejects.toBeInstanceOf(ApiError);
  });
});

describe("api client: commercial documents (S6)", () => {
  it("reads the registered types as names only and builds the list query without any client-supplied price or total", async () => {
    const fetchMock = vi.fn().mockImplementation((url: string) => Promise.resolve(json(url.endsWith("/types") ? { types: ["a_type"] } : { documents: [] })));
    const client = createApiClient("https://x", fetchMock as unknown as typeof fetch);
    expect(await client.listCommercialDocumentTypes()).toEqual(["a_type"]);
    await client.listCommercialDocuments({ status: 2, docType: "a_type", branchId: "b", offset: 25, limit: 25 });
    await client.listCommercialDocuments();
    expect(call(fetchMock, 0).url).toBe("https://x/api/v1/company/commercial-documents/types");
    expect(call(fetchMock, 1).url).toBe("https://x/api/v1/company/commercial-documents?status=2&docType=a_type&branchId=b&offset=25&limit=25");
    expect(call(fetchMock, 2).url).toBe("https://x/api/v1/company/commercial-documents");
  });

  it("sends the party, currency and number search to the server and reads the exact total", async () => {
    const fetchMock = vi.fn().mockImplementation(() => Promise.resolve(json({ documents: [{ id: "d" }], total: 7 })));
    const client = createApiClient("https://x", fetchMock as unknown as typeof fetch);
    const page = await client.searchCommercialDocuments({ status: 3, branchId: "b", partyId: "p", currency: "JOD", docType: "t", search: "07 7", offset: 50, limit: 50 });
    expect(page.total).toBe(7);
    expect(call(fetchMock, 0).url).toBe("https://x/api/v1/company/commercial-documents?status=3&docType=t&branchId=b&partyId=p&currency=JOD&search=07+7&offset=50&limit=50");
    expect(await client.listCommercialDocuments({ partyId: "p" })).toEqual([{ id: "d" }]);
    expect(call(fetchMock, 1).url).toBe("https://x/api/v1/company/commercial-documents?partyId=p");
  });

  it("sends the revision as If-Match on every change and the reason in the body of reject and cancel", async () => {
    const fetchMock = vi.fn().mockImplementation(() => Promise.resolve(json({ id: "d" })));
    const client = createApiClient("https://x", fetchMock as unknown as typeof fetch);
    await client.submitCommercialDocument("d", 3);
    await client.approveCommercialDocument("d", 4);
    await client.rejectCommercialDocument("d", "no", 5);
    await client.cancelCommercialDocument("d", "gone", 6);
    const heads = [0, 1, 2, 3].map((i) => (call(fetchMock, i).init.headers as Record<string, string>)["If-Match"]);
    expect(heads).toEqual(['"3"', '"4"', '"5"', '"6"']);
    expect(call(fetchMock, 0).url).toBe("https://x/api/v1/company/commercial-documents/d/submit");
    expect(JSON.parse(call(fetchMock, 2).init.body as string)).toEqual({ reason: "no" });
    expect(JSON.parse(call(fetchMock, 3).init.body as string)).toEqual({ reason: "gone" });
  });
});

describe("api client: party list filters", () => {
  it("sends active and withoutRole next to search, limit and offset, and nothing when absent", async () => {
    const fetchMock = vi.fn().mockImplementation(() => Promise.resolve(json({ parties: [], total: 0 })));
    const client = createApiClient("https://x", fetchMock as unknown as typeof fetch);
    await client.listParties({ search: "a b", limit: 50, active: true, withoutRole: "customer" });
    await client.listParties({ active: false, withoutRole: "supplier", offset: 5 });
    await client.listParties();
    expect(new URL(call(fetchMock, 0).url).searchParams.get("active")).toBe("true");
    expect(new URL(call(fetchMock, 0).url).searchParams.get("withoutRole")).toBe("customer");
    expect(new URL(call(fetchMock, 0).url).searchParams.get("search")).toBe("a b");
    expect(new URL(call(fetchMock, 1).url).searchParams.get("active")).toBe("false");   // false is sent, not dropped
    expect(new URL(call(fetchMock, 1).url).searchParams.get("withoutRole")).toBe("supplier");
    expect(call(fetchMock, 2).url).toBe("https://x/api/v1/company/parties");
  });
});

describe("api client: document links (S6 follow-up)", () => {
  it("posts a link to the links route, reads the links and the derived line quantities of a document", async () => {
    const fetchMock = vi.fn().mockImplementation((url: string) => Promise.resolve(json(url.endsWith("/links") && !url.includes("/d/") ? { id: "l" } : url.endsWith("/line-quantities") ? { lines: [{ lineNo: 1 }] } : { links: [{ id: "l" }] })));
    const client = createApiClient("https://x", fetchMock as unknown as typeof fetch);
    await client.createDocumentLink({ fromDocumentId: "a", fromLineNo: 1, toDocumentId: "b", toLineNo: 2, kind: 1, quantity: 3, idempotencyKey: "key-00001" });
    expect(await client.listDocumentLinks("d")).toEqual([{ id: "l" }]);
    expect(await client.getLineQuantities("d")).toEqual([{ lineNo: 1 }]);
    expect(call(fetchMock, 0).url).toBe("https://x/api/v1/company/commercial-documents/links");
    expect(JSON.parse(call(fetchMock, 0).init.body as string)).toEqual({ fromDocumentId: "a", fromLineNo: 1, toDocumentId: "b", toLineNo: 2, kind: 1, quantity: 3, idempotencyKey: "key-00001" });
    expect(call(fetchMock, 1).url).toBe("https://x/api/v1/company/commercial-documents/d/links");
    expect(call(fetchMock, 2).url).toBe("https://x/api/v1/company/commercial-documents/d/line-quantities");
  });

  it("reads the registered link pairs from the types route and treats an absent field as none", async () => {
    const fetchMock = vi.fn().mockResolvedValueOnce(json({ types: ["a"], linkPairs: [{ source: "a", target: "a", kind: 1 }] })).mockResolvedValueOnce(json({ types: [] }));
    const client = createApiClient("https://x", fetchMock as unknown as typeof fetch);
    expect(await client.listCommercialLinkPairs()).toEqual([{ source: "a", target: "a", kind: 1 }]);
    expect(await client.listCommercialLinkPairs()).toEqual([]);
    expect(call(fetchMock, 0).url).toBe("https://x/api/v1/company/commercial-documents/types");
  });
});

describe("api client: the second factor (Phase 4.1; one implementation for Web and Desktop)", () => {
  it("names the next step when the password is not enough, and keeps no bearer token", async () => {
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(json({ status: "mfa_required", challenge: "c1", challengeExpiresAtUtc: "2026-11-21T12:00:00Z" }))
      .mockResolvedValueOnce(json({ status: "mfa_enrolment_required", challenge: "c2" }))
      .mockResolvedValueOnce(json({ status: "mfa_recovery_required" }))
      .mockImplementation(() => Promise.resolve(json({ companies: [] })));
    const client = createApiClient("https://x", fetchMock as unknown as typeof fetch, { client: "desktop" });
    expect(await client.login("l", "p")).toEqual({ step: { kind: "mfa_required", challenge: "c1" } });
    expect(await client.login("l", "p")).toEqual({ step: { kind: "mfa_enrolment_required", challenge: "c2" } });
    expect(await client.login("l", "p")).toEqual({ step: { kind: "mfa_recovery_required" } });
    await client.listCompanies();
    expect((call(fetchMock, 3).init.headers as Record<string, string>)["Authorization"]).toBeUndefined();   // no session was created by any of them
  });

  it("completes a login with the challenge and the code, anonymously, and the desktop keeps the new token in memory", async () => {
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(json({ user: { id: "u", login: "l", displayName: "d", isIdentityAdministrator: false }, token: "tok" }))
      .mockImplementation(() => Promise.resolve(json({ companies: [] })));
    const client = createApiClient("https://x", fetchMock as unknown as typeof fetch, { client: "desktop" });
    const result = await client.verifyMfa("c1", "123456");
    expect(result).toMatchObject({ user: { id: "u" } });
    expect(call(fetchMock).url).toBe("https://x/api/v1/auth/mfa/verify");
    expect(JSON.parse(call(fetchMock).init.body as string)).toEqual({ challenge: "c1", code: "123456", client: "desktop" });
    await client.listCompanies();
    expect((call(fetchMock, 1).init.headers as Record<string, string>)["Authorization"]).toBe("Bearer tok");
  });

  it("does not notify the session-ended handler for a refused second step", async () => {
    const onUnauthenticated = vi.fn();
    const fetchMock = vi.fn().mockResolvedValue(json({ error: "invalid_credentials" }, { status: 401 }));
    const client = createApiClient("https://x", fetchMock as unknown as typeof fetch, { onUnauthenticated });
    await expect(client.verifyMfa("c", "000000")).rejects.toMatchObject({ status: 401 });
    await expect(client.beginEnrolment({ challenge: "c", enrolmentCode: "x" })).rejects.toMatchObject({ status: 401 });
    await expect(client.confirmEnrolment({ challenge: "c", code: "000000" })).rejects.toMatchObject({ status: 401 });
    expect(onUnauthenticated).not.toHaveBeenCalled();
  });

  it("begins an enrolment with the one-time code or, voluntarily, with the password, and confirms it", async () => {
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(json({ otpauthUri: "otpauth://totp/x?secret=AAAA", secret: "AAAA" }))
      .mockResolvedValueOnce(json({ otpauthUri: "otpauth://totp/x?secret=BBBB", secret: "BBBB" }))
      .mockResolvedValueOnce(json({ user: { id: "u", login: "l", displayName: "d", isIdentityAdministrator: false }, token: null }))
      .mockResolvedValueOnce(json({ user: { id: "u", login: "l", displayName: "d", isIdentityAdministrator: false }, token: null }));
    const client = createApiClient("https://x", fetchMock as unknown as typeof fetch);
    expect((await client.beginEnrolment({ challenge: "c", enrolmentCode: "CODE" })).secret).toBe("AAAA");
    expect(JSON.parse(call(fetchMock, 0).init.body as string)).toEqual({ challenge: "c", enrolmentCode: "CODE", client: "web" });
    expect((await client.beginEnrolment({ password: "pw" })).secret).toBe("BBBB");
    expect(JSON.parse(call(fetchMock, 1).init.body as string)).toEqual({ password: "pw", client: "web" });
    expect(await client.confirmEnrolment({ code: "123456" })).toMatchObject({ user: { id: "u" } });                 // a voluntary enrolment also ends in a NEW session
    expect(await client.confirmEnrolment({ challenge: "c", code: "123456" })).toMatchObject({ user: { id: "u" } });
    expect(call(fetchMock, 2).url).toBe("https://x/api/v1/auth/mfa/enrol/confirm");
  });
});
