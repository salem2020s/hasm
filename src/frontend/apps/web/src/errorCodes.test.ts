import { ApiError } from "@hasm/api-client";
import { readdirSync, readFileSync, statSync } from "node:fs";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import { errorText } from "./components";
import { catalog } from "./messages";

/**
 * Release 1 regression (Arabic/English parity of server refusals): every error code literal the API layer can return either has a
 * translation (any `...error.<code>` or `...field.<code>` key, in both languages) or is explicitly accepted below as falling back to
 * the generic, status-based message. The accepted list is closed: a new untranslated code fails this test.
 * LIMITATION: codes passed dynamically (e.g. Error(409, r.Code)) are not visible to this static scan; their translation is tested
 * by the screen that owns them.
 */
const apiDir = join(__dirname, "../../../../backend/Hasm.Api");
const sources = (d: string): string[] => readdirSync(d).flatMap((f) => {
  const p = join(d, f);
  if (statSync(p).isDirectory()) return /^(bin|obj)$/.test(f) ? [] : sources(p);
  return f.endsWith(".cs") ? [p] : [];
});

const serverCodes = (): string[] => {
  const found = new Set<string>();
  for (const f of sources(apiDir)) {
    const text = readFileSync(f, "utf8");
    for (const m of text.matchAll(/\b(?:Error|Bad|Conflict|Forbidden|NotFound)\(\s*(?:\d{3}\s*,\s*|"[A-Za-z]+"\s*,\s*)"([a-z][a-z0-9_]+)"/g)) found.add(m[1]!);
    for (const m of text.matchAll(/\berror\s*=\s*"([a-z][a-z0-9_]+)"/g)) found.add(m[1]!);
  }
  return [...found].sort();
};

/** Accepted generic fallbacks, with the reason. Nothing here is hidden: each is reported in the PR description. */
const GENERIC_FALLBACK: Record<string, string> = {
  unauthenticated: "401 is mapped to error.session_expired by status (a specific text would hide the sign-in prompt)",
  missing_request_header: "CSRF header is always sent by the shared api-client; not user-reachable",
};

const translated = (code: string, lang: "ar" | "en") =>
  Object.keys(catalog[lang]).some((k) => new RegExp(`(^|\\.)(error|field)(\\.[a-z_]+)*\\.${code}$`).test(k));

describe("server error codes are translated in Arabic and English", () => {
  const codes = serverCodes();

  it("discovers the codes the API layer can return", () => {
    expect(codes.length).toBeGreaterThan(25);
    expect(codes).toContain("revision_conflict");
  });

  it("covers every code, or lists it as an accepted generic fallback", () => {
    const missing = codes.filter((c) => !(translated(c, "ar") && translated(c, "en")) && !(c in GENERIC_FALLBACK));
    expect(missing).toEqual([]);
  });

  it("keeps the accepted list free of stale entries", () => {
    expect(Object.keys(GENERIC_FALLBACK).filter((c) => !codes.includes(c) || (translated(c, "ar") && translated(c, "en")))).toEqual([]);
  });

  it("has no code translated in one language only", () => {
    expect(codes.filter((c) => translated(c, "ar") !== translated(c, "en"))).toEqual([]);
  });

  it.each(["ar", "en"] as const)("falls back to a translated, status-based message for an unknown code (%s)", (lang) => {
    const t = (k: string) => catalog[lang][k] ?? k;
    const text = (status: number) => errorText(t, new ApiError(status, "totally_unknown_code", {}));
    expect(text(403)).toBe(catalog[lang]["error.forbidden"]);
    expect(text(401)).toBe(catalog[lang]["error.session_expired"]);
    expect(text(503)).toBe(catalog[lang]["error.company_unavailable"]);
    expect(text(500)).toBe(catalog[lang]["error.generic"]);
    expect(errorText(t, new Error("boom"))).toBe(catalog[lang]["error.network"]);
    for (const s of [403, 401, 503, 500]) expect(text(s)).not.toMatch(/^[a-z]+\.[a-z_.]+$/);
  });
});
