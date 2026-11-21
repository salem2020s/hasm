import { describe, expect, it } from "vitest";
import { fieldErrors } from "./components";
import { catalog } from "./messages";
import { apiError } from "./test-utils";

/**
 * Catalog-wide uniqueness: messages.ts merges every module catalog with Object.assign, so a key defined in two modules is silently overwritten by the later one
 * (a screen then shows another screen's wording). Every `messages.*.ts` module exports `<name>Ar` / `<name>En` records; no key may appear in two modules of the same language.
 * A screen that needs different wording for a shared server code uses a scoped key (`field.<scope>.<code>`, see fieldErrors).
 */
const modules = import.meta.glob<Record<string, unknown>>(["./messages.*.ts", "!./messages.test.ts"], { eager: true });

describe("message catalog modules", () => {
  const bylang: Record<"Ar" | "En", Map<string, string[]>> = { Ar: new Map(), En: new Map() };
  for (const [file, mod] of Object.entries(modules)) {
    for (const [name, value] of Object.entries(mod)) {
      const lang = name.endsWith("Ar") ? "Ar" : name.endsWith("En") ? "En" : null;
      if (!lang || typeof value !== "object" || value === null) continue;
      for (const key of Object.keys(value)) bylang[lang].set(key, [...(bylang[lang].get(key) ?? []), file]);
    }
  }

  it("discovers the module catalogs (guards against the glob silently matching nothing)", () => {
    expect(bylang.Ar.size).toBeGreaterThan(500);
    expect(bylang.En.size).toBeGreaterThan(500);
    expect(bylang.Ar.get("appName")).toEqual(["./messages.base.ts"]);          // the base catalog (shell, accounts, shared field messages) is covered too
    expect(bylang.En.get("field.too_long")).toEqual(["./messages.base.ts"]);
  });

  it("has no key defined in two modules (per language)", () => {
    for (const lang of ["Ar", "En"] as const) {
      const dup = [...bylang[lang]].filter(([, files]) => files.length > 1).map(([k, files]) => `${k}: ${files.join(", ")}`);
      expect(dup, `${lang} duplicates`).toEqual([]);
    }
  });

  it("keeps ar and en keys identical across the merged catalog", () => {
    expect(Object.keys(catalog.ar).sort()).toEqual(Object.keys(catalog.en).sort());
  });

  it("scoped field lookups keep each screen's wording: attachments say 'text', everything else keeps the shared 'value' wording", () => {
    const t = (lang: "ar" | "en") => (k: string) => catalog[lang][k] ?? k;
    const err = apiError(400, "validation_failed", { fields: [{ field: "reason", code: "too_long" }] });
    expect(fieldErrors(t("en"), err)).toEqual({ reason: "The value is longer than allowed." });
    expect(fieldErrors(t("en"), err, "att")).toEqual({ reason: "The text is longer than allowed." });
    expect(fieldErrors(t("ar"), err, "att").reason).toBe(catalog.ar["field.att.too_long"]);
    expect(fieldErrors(t("ar"), err, "att").reason).not.toBe(fieldErrors(t("ar"), err).reason);
    expect(fieldErrors(t("en"), err, "pr")).toEqual({ reason: "The value is longer than allowed." });   // a scope without its own key falls back to the shared one
  });
});
