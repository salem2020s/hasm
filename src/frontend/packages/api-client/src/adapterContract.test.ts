// @vitest-environment jsdom
// Adapter contract: the Web client (HttpOnly session cookie) and the Desktop client (opaque bearer token kept in memory) are two TRANSPORTS of ONE client. Everything a caller can observe must be identical:
// results, errors, the 401 callback, request paths, methods, bodies and headers other than the credential. Only the credential differs, and it must differ in exactly the documented way.
import { describe, expect, it, vi } from "vitest";
import { ApiError, createApiClient, type ClientKind } from "./index";

const KINDS: ClientKind[] = ["web", "desktop"];
const json = (body: unknown, init: ResponseInit = {}) => new Response(JSON.stringify(body), { headers: { "Content-Type": "application/json" }, ...init });
const USER = { id: "u", login: "l", displayName: "d", isIdentityAdministrator: false };
interface Sent { url: string; method: string; credentials: RequestCredentials | undefined; authorization: string | undefined; headers: Record<string, string>; body: unknown }

/** A fake server, identical for both kinds: it answers by path and (for the login) hands out a token that only the desktop client is expected to use. */
function harness(kind: ClientKind, script: (url: string, init: RequestInit) => Response | Promise<Response> = () => json({})) {
  const sent: Sent[] = [];
  const fetchMock = vi.fn().mockImplementation(async (url: string, init: RequestInit) => {
    const headers = { ...(init.headers as Record<string, string>) };
    sent.push({ url, method: init.method ?? "GET", credentials: init.credentials, authorization: headers["Authorization"], headers, body: typeof init.body === "string" ? JSON.parse(init.body) : init.body });
    if (url.endsWith("/api/v1/auth/login")) return json({ user: USER, token: "opaque-token" });                // the server may send a token; only the desktop transport may keep it
    return script(url, init);
  });
  const unauth = vi.fn();
  const client = createApiClient("https://x", fetchMock as unknown as typeof fetch, { client: kind, onUnauthenticated: unauth });
  return { client, sent, unauth, fetchMock };
}
const withoutTransport = (s: Sent) => ({ url: s.url, method: s.method, headers: Object.fromEntries(Object.entries(s.headers).filter(([k]) => k !== "Authorization")), body: s.url.endsWith("/auth/login") ? { ...(s.body as object), client: "<kind>" } : s.body });

describe.each(KINDS)("%s transport", (kind) => {
  it("sends credentials the way its transport requires, and never the other one", async () => {
    const { client, sent } = harness(kind, () => json({ companies: [] }));
    await client.login("l", "p");
    await client.listCompanies();
    await client.selectCompany("c1");
    const [login, ...rest] = sent;
    expect(login!.authorization).toBeUndefined();                                                    // the login carries no credential
    expect(login!.credentials).toBe(kind === "web" ? "include" : "omit");
    expect(login!.body).toMatchObject({ login: "l", password: "p", client: kind });                  // the client announces its transport
    for (const r of rest) {
      expect(r.credentials, r.url).toBe(kind === "web" ? "include" : "omit");
      expect(r.authorization, r.url).toBe(kind === "web" ? undefined : "Bearer opaque-token");
    }
  });

  it("keeps no credential in browser storage and does not expose the token to callers", async () => {
    const setItem = vi.spyOn(Storage.prototype, "setItem");
    const { client } = harness(kind);
    const result = await client.login("l", "p");
    expect(result).toEqual({ user: USER });                                                           // exactly the user; no token field
    expect(JSON.stringify(result)).not.toContain("opaque-token");
    expect(setItem).not.toHaveBeenCalled();
    expect(localStorage.length + sessionStorage.length).toBe(0);
    expect(document.cookie).toBe("");
    setItem.mockRestore();
  });

  it("a logout (even a failed one) forgets the credential: later calls send none", async () => {
    const { client, sent } = harness(kind, (url) => (url.endsWith("/auth/logout") ? json({ error: "boom" }, { status: 500 }) : json({ companies: [] })));
    await client.login("l", "p");
    await client.logout().catch(() => undefined);
    await client.listCompanies();
    expect(sent.at(-1)!.authorization).toBeUndefined();
  });

  it("downloads and uploads follow the same transport rule as every other call", async () => {
    const { client, sent } = harness(kind, (url) => (url.includes("/content") || url.includes("/reports/") ? new Response("csv", { status: 200 }) : json({ id: "a1" }, { status: 201 })));
    await client.login("l", "p");
    await client.downloadAttachment("a1");
    await client.uploadAttachment("local_party", "p1", new Blob(["x"]), "f.pdf", false);
    await client.uploadImport("inventory-units", new Blob(["code,name\nA,B\n"]), "units.csv");
    expect(sent.some((r) => r.url.includes("/api/v1/company/imports?"))).toBe(true);                    // the import upload really went through this check
    for (const r of sent.slice(1)) {
      expect(r.credentials, r.url).toBe(kind === "web" ? "include" : "omit");
      expect(r.authorization, r.url).toBe(kind === "web" ? undefined : "Bearer opaque-token");
    }
    for (const r of sent.slice(1).filter((x) => x.method === "POST")) expect(r.headers["X-Hasm-Request"], r.url).toBe("1");   // an upload is a state change: the anti-CSRF header is required
  });
});

describe("web and desktop are observably identical", () => {
  /** Runs the same script on each kind and returns what a caller could observe. */
  async function observe(run: (c: ReturnType<typeof harness>["client"]) => Promise<unknown>, script?: (url: string, init: RequestInit) => Response | Promise<Response>) {
    const out = {} as Record<ClientKind, { result: unknown; sent: ReturnType<typeof withoutTransport>[]; unauth: number }>;
    for (const kind of KINDS) {
      const h = harness(kind, script);
      await h.client.login("l", "p");
      const result = await run(h.client).then((r) => ({ ok: r }), (e) => ({ error: e instanceof ApiError ? { name: e.name, status: e.status, code: e.code, details: e.details } : String(e) }));
      out[kind] = { result, sent: h.sent.map(withoutTransport), unauth: h.unauth.mock.calls.length };
    }
    return out;
  }

  it("a successful read and a state change produce the same result and the same requests (path, method, headers, body) apart from the credential", async () => {
    const o = await observe(async (c) => { const list = await c.listCompanies(); const sel = await c.selectCompany("c1"); await c.clearCompany(); return { list, sel }; },
      (url, init) => (init.method === "DELETE" ? new Response(null, { status: 204 }) : url.endsWith("/companies") ? json({ companies: [{ id: "c1", nameAr: "ا", nameEn: "A" }] }) : json({ companyId: "c1" })));
    expect(o.desktop).toEqual(o.web);
    expect(o.web.sent.map((s) => `${s.method} ${s.url}`)).toEqual(["POST https://x/api/v1/auth/login", "GET https://x/api/v1/companies", "POST https://x/api/v1/session/company", "DELETE https://x/api/v1/session/company"]);
    expect(o.web.sent.filter((s) => s.method !== "GET").every((s) => s.headers["X-Hasm-Request"] === "1")).toBe(true);
  });

  it.each([
    [400, { error: "invalid_request", fields: [{ field: "code", code: "required" }] }],
    [403, { error: "forbidden" }],
    [404, { error: "not_found" }],
    [412, { error: "revision_conflict", currentRevision: 7 }],
    [409, { error: "conflict" }],
    [500, { error: "internal" }],
  ])("a %s answer becomes the same ApiError (status, code, fields, revision) and never ends the session", async (status, body) => {
    const o = await observe((c) => c.listCompanies(), () => json(body, { status }));
    expect(o.desktop).toEqual(o.web);
    expect((o.web.result as { error: { status: number } }).error.status).toBe(status);
    expect(o.web.unauth).toBe(0);
    expect(o.desktop.unauth).toBe(0);
  });

  it("an expired session (401 on an authenticated call) raises the callback exactly once on both, with the same error", async () => {
    const o = await observe((c) => c.listCompanies(), () => json({ error: "unauthenticated" }, { status: 401 }));
    expect(o.desktop).toEqual(o.web);
    expect(o.web.unauth).toBe(1);
    expect((o.web.result as { error: { status: number; code: string } }).error).toMatchObject({ status: 401, code: "unauthenticated" });
  });

  it("a refused sign-in (401 on the anonymous login) does NOT raise the session-expired callback on either transport", async () => {
    for (const kind of KINDS) {
      const unauth = vi.fn();
      const fetchMock = vi.fn().mockResolvedValue(json({ error: "invalid_credentials" }, { status: 401 }));
      const client = createApiClient("https://x", fetchMock as unknown as typeof fetch, { client: kind, onUnauthenticated: unauth });
      await expect(client.login("l", "bad")).rejects.toMatchObject({ status: 401, code: "invalid_credentials" });
      expect(unauth).not.toHaveBeenCalled();
      expect((fetchMock.mock.calls[0]![1] as RequestInit).headers).not.toHaveProperty("Authorization");
    }
  });

  it("a non-JSON failure body and a Retry-After header are reported the same way", async () => {
    const o = await observe((c) => c.listCompanies(), () => new Response("<html>bad gateway</html>", { status: 503, headers: { "Retry-After": "30" } }));
    expect(o.desktop).toEqual(o.web);
    expect((o.web.result as { error: { code: string; details: { retryAfterSeconds?: number } } }).error).toMatchObject({ code: "request_failed", details: { retryAfterSeconds: 30 } });
  });
});
