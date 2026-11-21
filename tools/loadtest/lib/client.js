"use strict";
// Thin HTTP client over the real HASM API. It signs in as the Desktop client (bearer token), so it works over plain loopback HTTP as well as
// HTTPS; it sends the anti-CSRF header on every state change; it never disables certificate validation (use NODE_EXTRA_CA_CERTS for a
// development certificate).
const { ENDPOINTS, path } = require("./endpoints");

class ApiError extends Error {
  constructor(status, body, where) {
    super(`${where} -> ${status} ${body && body.error ? body.error : ""}${body && body.fields ? " " + JSON.stringify(body.fields) : ""}`.trim());
    this.status = status; this.body = body; this.code = body && body.error;
  }
}

class Client {
  constructor({ baseUrl, timeoutMs = 30000, fetchImpl = globalThis.fetch }) {
    this.base = baseUrl.replace(/\/+$/, ""); this.timeoutMs = timeoutMs; this.fetch = fetchImpl; this.token = null;
  }
  /** Returns { status, json, etag, ms }. Does not throw on HTTP errors (the caller decides); throws on network failure or timeout. */
  async request(method, url, { body, ifMatch, query } = {}) {
    const qs = query ? "?" + new URLSearchParams(Object.entries(query).filter(([, v]) => v !== undefined && v !== null)).toString() : "";
    const headers = { Accept: "application/json" };
    if (method !== "GET") headers["X-Hasm-Request"] = "1";
    if (body !== undefined) headers["Content-Type"] = "application/json";
    if (ifMatch !== undefined) headers["If-Match"] = `"${ifMatch}"`;
    if (this.token) headers.Authorization = `Bearer ${this.token}`;
    const t0 = process.hrtime.bigint();
    const res = await this.fetch(this.base + url + qs, { method, headers, body: body === undefined ? undefined : JSON.stringify(body), signal: AbortSignal.timeout(this.timeoutMs) });
    const text = await res.text();
    const ms = Number(process.hrtime.bigint() - t0) / 1e6;
    let json = null; try { json = text ? JSON.parse(text) : null; } catch { /* non-JSON body */ }
    const etag = res.headers.get("etag"); // "\"3\""
    return { status: res.status, json, ms, etag: etag ? Number(etag.replace(/"/g, "")) : null };
  }
  /** Like request, but 2xx or ApiError. */
  async call(name, params, opts) {
    const [method] = ENDPOINTS[name];
    const r = await this.request(method, path(name, params), opts);
    if (r.status < 200 || r.status >= 300) throw new ApiError(r.status, r.json, `${name}`);
    return r;
  }
  async login(login, password) {
    const r = await this.request("POST", path("login"), { body: { login, password, client: "desktop" } });
    if (r.status !== 200 || !r.json || !r.json.token) throw new ApiError(r.status, r.json, "login");
    this.token = r.json.token;
  }
  async selectCompany(companyId) {
    const r = await this.request("POST", path("selectCompany"), { body: { companyId } });
    if (r.status !== 200) throw new ApiError(r.status, r.json, "selectCompany");
  }
}
module.exports = { Client, ApiError };
