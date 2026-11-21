import { ApiError } from "@hasm/api-client";
import { fireEvent, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { catalog } from "./messages";
import { allPermissions, emptyClient, readRoutes, type RouteRule } from "./screenHarness";
import { apiError, renderApp, signedIn } from "./test-utils";

/**
 * Release 1 regression (RBAC, session and draft behaviour on the shared screens, identical on Web and Desktop):
 *  - every guarded route shows a translated "forbidden" state, never the screen, without its permission;
 *  - the same route renders with just that permission;
 *  - a server 403 or 401 while a screen loads is answered with a translated state (never raw keys or server text);
 *  - a failed save (403, network) keeps everything the user typed.
 * Authorization itself is enforced by the server; this proves the UI never contradicts it.
 */
const routes = readRoutes();
const guarded = routes.filter((r) => r.permission || r.anyOf || r.anyScope || r.centralAdmin);
const everything = allPermissions(routes);

const needs = (r: RouteRule): string[] => (r.permission ? [r.permission] : r.anyOf ? [r.anyOf[0]!] : r.anyScope ? [r.anyScope] : []);

const go = async (hash: string) => {
  await screen.findByTestId("company-name");
  window.location.hash = hash;
  fireEvent(window, new HashChangeEvent("hashchange"));
};

const BOOTSTRAP = new Set(["getHealth", "login", "logout", "status", "listCompanies", "selectCompany", "clearCompany", "accessMe"]);
/** Every call except the sign-in and access bootstrap answers with the given failure, so each screen meets the refusal on its own load. */
const failing = (status: number, code: string, permissions: string[]) => {
  const base = signedIn({}, permissions) as unknown as Record<string, unknown>;
  return new Proxy(base, {
    get: (t, k: string) => (BOOTSTRAP.has(k) ? t[k] : vi.fn().mockRejectedValue(new ApiError(status, code, {}))),
  }) as unknown as ReturnType<typeof signedIn>;
};

const RAW = /^[a-z][a-z0-9]*(?:[._][a-z0-9]+)*\.[a-z0-9_.]+$/;
const textNodes = (root: HTMLElement) => {
  const out: string[] = [];
  for (let w = document.createTreeWalker(root, NodeFilter.SHOW_TEXT), n = w.nextNode(); n; n = w.nextNode()) out.push((n.textContent ?? "").trim());
  return out.filter(Boolean);
};

beforeEach(() => { localStorage.clear(); window.location.hash = ""; vi.spyOn(console, "error").mockImplementation(() => {}); });
afterEach(() => { vi.restoreAllMocks(); });

describe("route permission gates (both languages)", () => {
  it("covers a meaningful number of guarded routes", () => expect(guarded.length).toBeGreaterThan(25));

  it("leaves only the home page and the user's own security settings unguarded (removing a guard from any other route fails here)", () => {
    // /account/security is an ACCOUNT screen, not a company one: every signed-in user may open it, there is no company permission to hold; the server decides each action by the session and the password.
    expect(routes.filter((r) => !guarded.includes(r)).map((r) => r.path)).toEqual(["/", "/account/security"]);
  });

  for (const lang of ["ar", "en"] as const) {
    it.each(guarded.map((r) => r.path))(`${lang}: %s is forbidden without its permission`, async (path) => {
      localStorage.setItem("hasm.locale", lang);
      const rule = guarded.find((r) => r.path === path)!;
      const without = everything.filter((p) => !needs(rule).includes(p) && !(rule.anyOf ?? []).includes(p));
      renderApp(signedIn({}, rule.centralAdmin ? everything : without));
      await go(`#${path}`);
      const banner = await screen.findByTestId("forbidden");
      expect(banner).toHaveTextContent(catalog[lang]["error.forbidden"]!);
    });

    it.each(guarded.filter((r) => !r.centralAdmin).map((r) => r.path))(`${lang}: %s opens with just its permission`, async (path) => {
      localStorage.setItem("hasm.locale", lang);
      const rule = guarded.find((r) => r.path === path)!;
      renderApp(emptyClient(needs(rule)));
      await go(`#${path}`);
      await waitFor(() => expect(document.getElementById("content")!.textContent!.trim().length).toBeGreaterThan(0));
      expect(screen.queryByTestId("forbidden")).toBeNull();
      expect(screen.queryByTestId("not-found")).toBeNull();
    });
  }

  it("a non-administrator is refused the central-administrator route even holding every permission", async () => {
    renderApp(signedIn({}, everything));
    await go("#/admin/party-proposals");
    expect(await screen.findByTestId("forbidden")).toBeInTheDocument();
  });

  it("an unknown route is a translated not-found state, not a blank page", async () => {
    renderApp(signedIn({}, everything));
    await go("#/no/such/screen");
    expect(await screen.findByTestId("not-found")).toBeInTheDocument();
  });
});

describe("server refusals while a screen loads (both languages)", () => {
  for (const lang of ["ar", "en"] as const) {
    it.each(routes.map((r) => r.path))(`${lang}: %s answers a server 403 with translated text only`, async (path) => {
      localStorage.setItem("hasm.locale", lang);
      renderApp(failing(403, "forbidden", everything));
      await go(`#${path}`);
      const content = document.getElementById("content")!;
      // Positive assertion on a stable loaded state: the screen shows the translated refusal (the shared one or its own wording of it).
      // Home states its own "activity unavailable" wording; the report screens run only on demand and make no call when they open,
      // so their stable loaded state is their own heading.
      const own: Record<string, string> = { "/": "dash.activity_unavailable", "/accounting/reports": "rep.title", "/accounting/statements": "st.title", "/account/security": "sec.mfa.unknown_note" };   // the only call it makes on open is /auth/status (an access-bootstrap call that is not refused here): its stable state is the "no state reported" note
      const refusals = (own[path] ? [own[path]] : ["error.forbidden", "rep.error.forbidden", "sm.error.forbidden"]).map((k) => catalog[lang][k]!);
      await waitFor(() => expect(refusals.some((r) => content.textContent!.includes(r)), `${path}: ${content.textContent?.slice(0, 120)}`).toBe(true), { timeout: 3000 });
      expect(document.querySelector("[aria-busy='true']")).toBeNull();
      expect(screen.queryByTestId("not-found")).toBeNull();
      expect(textNodes(content).filter((s) => RAW.test(s) && !s.endsWith("."))).toEqual([]);
      expect(content.textContent).not.toMatch(/forbidden|Forbidden/);
    });

    it(`${lang}: a server 401 on a screen ends the session with the translated notice`, async () => {
      localStorage.setItem("hasm.locale", lang);
      renderApp(failing(401, "unauthenticated", everything));
      await go("#/company/taxes");
      expect(await screen.findByText(catalog[lang]["login.session_expired"]!)).toBeInTheDocument();
      expect(screen.queryByTestId("company-name")).toBeNull();
    });
  }
});

describe("a failed save never loses what the user typed", () => {
  const stored = { nameAr: "شركة الأمل", nameEn: null, legalName: null, commercialRegistrationNo: null, taxRegistrationNo: null, countryCode: "SA",
    addressLine1: null, addressLine2: null, city: "Riyadh", region: null, postalCode: null, phone: null, email: null, website: null };
  const getProfile = () => vi.fn().mockResolvedValue({ profile: stored, revision: 1, updatedAtUtc: null, projection: "synced" });

  for (const [label, error, expected] of [
    ["403 forbidden", () => apiError(403, "forbidden"), "error.forbidden"],
    ["network failure", () => new TypeError("Failed to fetch"), "error.network"],
    ["503 company unavailable", () => apiError(503, "company_unavailable"), "error.company_unavailable"],
  ] as const) {
    it.each(["ar", "en"] as const)(`${label} (%s): edits stay, translated message shown, nothing saved`, async (lang) => {
      localStorage.setItem("hasm.locale", lang);
      const putProfile = vi.fn().mockRejectedValue(error());
      renderApp(signedIn({ getProfile: getProfile(), putProfile }));
      await go("#/company/profile");
      const city = await screen.findByLabelText(lang === "ar" ? "المدينة" : "City");
      await userEvent.clear(city);
      await userEvent.type(city, "Jeddah");
      await userEvent.click(screen.getByRole("button", { name: lang === "ar" ? "حفظ" : "Save" }));
      await waitFor(() => expect(putProfile).toHaveBeenCalledTimes(1));
      expect(await screen.findByText(catalog[lang][expected]!)).toBeInTheDocument();
      expect(screen.getByLabelText(lang === "ar" ? "المدينة" : "City")).toHaveValue("Jeddah");
      expect(screen.queryByTestId("saved")).toBeNull();
    });
  }

  it("a save answered 401 ends the session with the expired notice (the server rejected the credential; nothing is claimed as saved)", async () => {
    const putProfile = vi.fn().mockRejectedValue(apiError(401, "unauthenticated"));
    renderApp(signedIn({ getProfile: getProfile(), putProfile }));
    await go("#/company/profile");
    const city = await screen.findByLabelText("المدينة");
    await userEvent.clear(city);
    await userEvent.type(city, "Jeddah");
    await userEvent.click(screen.getByRole("button", { name: "حفظ" }));
    expect(await screen.findByText(catalog.ar["login.session_expired"]!)).toBeInTheDocument();
    expect(screen.queryByTestId("saved")).toBeNull();
  });
});
