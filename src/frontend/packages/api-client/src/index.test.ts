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
