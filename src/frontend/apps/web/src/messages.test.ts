import { readdirSync, readFileSync, statSync } from "node:fs";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import { catalog } from "./messages";

const sourceFiles = (dir: string): string[] =>
  readdirSync(dir).flatMap((f) => {
    const p = join(dir, f);
    return statSync(p).isDirectory() ? sourceFiles(p) : /\.tsx?$/.test(f) && !/\.test\./.test(f) && f !== "messages.ts" && f !== "test-utils.tsx" ? [p] : [];
  });

describe("message catalog", () => {
  it("has identical keys in Arabic and English", () => {
    expect(Object.keys(catalog.ar).sort()).toEqual(Object.keys(catalog.en).sort());
  });

  it("has no empty translations", () => {
    for (const lang of ["ar", "en"] as const) for (const [k, v] of Object.entries(catalog[lang])) expect(v.trim(), `${lang}:${k}`).not.toBe("");
  });

  it("defines every literal translation key used by the screens", () => {
    const used = new Set<string>();
    for (const file of sourceFiles(join(__dirname))) {
      const text = readFileSync(file, "utf8");
      for (const m of text.matchAll(/\bt\(\s*["']([A-Za-z0-9_.-]+)["']/g)) used.add(m[1]);
    }
    const missing = [...used].filter((k) => !(k in catalog.en));
    expect(missing).toEqual([]);
  });

  it("defines a field-error text for every validation code the server can return", () => {
    for (const code of ["required", "name_required", "too_long", "invalid_characters", "invalid_format", "invalid_url", "unknown_country", "unsupported_value",
      "unknown_time_zone", "invalid_currency_code", "unknown_currency", "currency_not_active", "base_currency_must_be_enabled", "base_currency_cannot_be_unset", "base_currency_required_first"])
      expect(`field.${code}` in catalog.ar && `field.${code}` in catalog.en, code).toBe(true);
  });

  it("has a translated message for each server error code the screens can meet", () => {
    for (const code of ["invalid_credentials", "too_many_attempts", "entitlement_denied", "base_currency_locked", "setting_locked", "audit_unavailable", "company_unavailable", "forbidden"])
      expect(`error.${code}` in catalog.ar && `error.${code}` in catalog.en, code).toBe(true);
  });
});
