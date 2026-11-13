// @vitest-environment jsdom
// Storage/localStorage are not Node globals on Node 24 (CI), only on newer Node (local); jsdom provides
// them so the "no browser persistence" assertions run identically everywhere.
import { describe, expect, it, vi } from "vitest";
import { ApiError, createApiClient } from "./index";

const json = (body: unknown, init: ResponseInit = {}) => new Response(JSON.stringify(body), { headers: { "Content-Type": "application/json" }, ...init });
const call = (fetchMock: ReturnType<typeof vi.fn>, n = 0) => ({ url: fetchMock.mock.calls[n][0] as string, init: fetchMock.mock.calls[n][1] as RequestInit });

describe("api client", () => {
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
