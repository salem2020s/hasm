import { readdirSync, readFileSync, statSync } from "node:fs";
import { join } from "node:path";
import { describe, expect, it } from "vitest";

/**
 * Release 1 regression (Arabic/English parity): user-visible text in screens comes only from the message catalog. This scans the screen
 * sources for (a) Arabic letters in code and (b) literal English text inside JSX or in user-facing attributes (aria-label, title,
 * placeholder, alt, label). Developer-only previews are excluded because they are removed from production bundles (import.meta.env.DEV).
 * Known violations must be listed in KNOWN with a reason; none may be added silently.
 */
const root = __dirname;
const SKIP_DIRS = new Set(["previews", "visual", "fixtures"]);
const files = (dir: string): string[] =>
  readdirSync(dir).flatMap((f) => {
    const p = join(dir, f);
    if (statSync(p).isDirectory()) return SKIP_DIRS.has(f) ? [] : files(p);
    return /\.tsx$/.test(f) && !/\.test\./.test(f) && f !== "test-utils.tsx" ? [p] : [];
  });

/**
 * Accepted exceptions, keyed by "file text-of-the-line" (no line numbers, so they survive edits). Each carries its reason.
 * Developer-only entries are gated by import.meta.env.DEV and removed from production bundles.
 */
const KNOWN: Record<string, string> = {
  'App.tsx id: "dev", label: locale === "ar" ? "معاينات التصميم (تطوير)" : "Design previews (DEV)", icon: <Sett': "DEV-only preview menu, absent from production bundles",
  'App.tsx { id: "/dev/preview", label: locale === "ar" ? "معرض التصميم" : "Design gallery" },': "DEV-only preview menu",
  'App.tsx { id: "/dev/preview/warehouses", label: locale === "ar" ? "معاينة المستودعات" : "Warehouses preview"': "DEV-only preview menu",
  'App.tsx { id: "/dev/preview/sales-invoices", label: locale === "ar" ? "معاينة فواتير المبيعات" : "Sales invo': "DEV-only preview menu",
};
const DIGIT_CONVERSION = /\[٠-٩\]/; // the Arabic-Indic digit range in a normalising regex is data handling, not UI text

const stripComments = (s: string) => s.replace(/\/\*[\s\S]*?\*\//g, (m) => m.replace(/[^\n]/g, " ")).replace(/(^|[^:])\/\/[^\n]*/g, (m, a) => a + " ".repeat(m.length - a.length));

export function findLiterals(source: string): { line: number; text: string }[] {
  const code = stripComments(source);
  const out: { line: number; text: string }[] = [];
  code.split("\n").forEach((ln, i) => {
    const arabic = /[\u0600-\u06FF]/.exec(ln);
    if (arabic && !DIGIT_CONVERSION.test(ln)) out.push({ line: i + 1, text: ln.trim().slice(0, 100) });
    for (const m of ln.matchAll(/\b(?:aria-label|title|placeholder|alt|label)="([^"{}]*[A-Za-z]{2,}[^"{}]*)"/g)) out.push({ line: i + 1, text: m[0] });
    // JSX text: >Some words< on one line, containing a letter sequence, not an expression.
    for (const m of ln.matchAll(/<[A-Za-z][\w.]*(?:\s[^<>]*)?>([^<>{}=;()&]*[A-Za-z]{3,}[^<>{}=;()&]*)</g)) {
      const t = m[1]!.trim();
      if (t && !/^[A-Z0-9_.\-/: ]+$/.test(t) && !/^[\w$]+(\.[\w$]+)+$/.test(t)) out.push({ line: i + 1, text: `>${t}<` });
    }
  });
  return out;
}

describe("no hard-coded user-visible text in the screens", () => {
  it("detects literals (self-check)", () => {
    expect(findLiterals('<button aria-label="Save now">x</button>').length).toBe(1);
    expect(findLiterals("<p>Hello world</p>").length).toBe(1);
    expect(findLiterals("<p>مرحبا</p>").length).toBe(1);
    expect(findLiterals('<p>{t("a.b")}</p> // Arabic: مرحبا').length).toBe(0);
    expect(findLiterals("const x: Array<string> = [];").length).toBe(0);
  });

  it("finds none beyond the recorded exceptions", () => {
    const hits: string[] = [];
    for (const f of files(root)) {
      const rel = f.slice(root.length + 1).replace(/\\/g, "/");
      for (const h of findLiterals(readFileSync(f, "utf8"))) {
        const id = `${rel} ${h.text}`;
        if (!(id in KNOWN)) hits.push(id);
      }
    }
    expect(hits).toEqual([]);
    // and no exception may go stale
  });

  it("keeps the exception list free of stale entries", () => {
    const seen = new Set<string>();
    for (const f of files(root)) for (const h of findLiterals(readFileSync(f, "utf8"))) seen.add(`${f.slice(root.length + 1).replace(/\\/g, "/")} ${h.text}`);
    expect(Object.keys(KNOWN).filter((k) => !seen.has(k))).toEqual([]);
  });
});
