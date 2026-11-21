import { fireEvent, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { allPermissions, emptyClient, readRoutes } from "./screenHarness";
import { renderApp } from "./test-utils";

/**
 * Release 1 regression (Arabic/English parity, shared Web/Desktop screens): every routed screen renders in Arabic (RTL) and in English (LTR)
 * with the same behaviour, shows no untranslated catalog key, and never crashes. API methods the test does not stub reject with a server
 * error, so each screen is also exercised in its translated error state.
 */
const routes = readRoutes();
const permissions = allPermissions(routes);

const RAW_KEY = /^[a-z][a-z0-9]*(?:[._-][a-z0-9]+)*\.[a-z0-9_.-]+$/;
const rawKeys = (root: HTMLElement) => {
  const found: string[] = [];
  const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
  for (let n = walker.nextNode(); n; n = walker.nextNode()) {
    const s = (n.textContent ?? "").trim();
    // A key ending in "." is a dynamic key whose suffix the empty fake answer left blank (test artefact), not a missing translation.
    if (s && RAW_KEY.test(s) && !s.endsWith(".") && !/^\d/.test(s) && !/\.(com|org|sa)$/.test(s)) found.push(s);
  }
  return found;
};

let errors: ReturnType<typeof vi.spyOn>;
beforeEach(() => { localStorage.clear(); window.location.hash = ""; errors = vi.spyOn(console, "error").mockImplementation(() => {}); });
afterEach(() => { errors.mockRestore(); });

describe("raw key detector", () => {
  it("flags catalog keys and ignores prose", () => {
    const d = document.createElement("div");
    d.innerHTML = "<p>accounts.title</p><p>Chart of accounts</p><p>jrn.error.some_code</p><p>شركة</p><p>1.5</p>";
    expect(rawKeys(d)).toEqual(["accounts.title", "jrn.error.some_code"]);
  });
});

describe("every routed screen in both languages", () => {
  it("discovers the real route table", () => {
    expect(routes.length).toBeGreaterThan(30);
    expect(permissions.length).toBeGreaterThan(30);
  });

  for (const [locale, dir] of [["ar", "rtl"], ["en", "ltr"]] as const) {
    it.each(routes.map((r) => r.path))(`renders %s in ${locale} (${dir}) with no raw keys and no crash`, async (path) => {
      localStorage.setItem("hasm.locale", locale);
      const c = emptyClient(permissions);
      (c as unknown as { status: ReturnType<typeof vi.fn> }).status = vi.fn().mockResolvedValue({
        authenticated: true, user: { id: "u1", login: "root", displayName: "Root", isIdentityAdministrator: true },
      });
      renderApp(c);
      await screen.findByTestId("company-name");
      window.location.hash = `#${path}`;
      fireEvent(window, new HashChangeEvent("hashchange"));
      await waitFor(() => expect(document.getElementById("content")!.textContent!.trim().length).toBeGreaterThan(0));
      // let the screen's own loading settle
      await waitFor(() => expect(document.querySelector("[aria-busy='true']")).toBeNull(), { timeout: 3000 }).catch(() => {});
      expect(document.documentElement.dir).toBe(dir);
      expect(document.documentElement.lang).toBe(locale);
      expect(screen.queryByTestId("forbidden")).toBeNull();
      expect(screen.queryByTestId("not-found")).toBeNull();
      expect(document.getElementById("content")!.textContent!.trim().length).toBeGreaterThan(0);
      expect(rawKeys(document.body)).toEqual([]);
      expect(errors.mock.calls.filter((a: unknown[]) => /crash|Error: |Uncaught|not a function/.test(String(a[0]))).map((a: unknown[]) => String(a[0]).slice(0, 160))).toEqual([]);
    });
  }
});
