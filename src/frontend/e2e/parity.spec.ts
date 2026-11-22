import { expect, test, type Page } from "@playwright/test";
import { catalog } from "../apps/web/src/messages";
import { MockApi, PASSWORD, type Mode } from "./mockApi";

/**
 * Web and Desktop parity, end to end, against the REAL production bundle and a stateful mock API (e2e/mockApi.ts). The desktop client is the same bundle with the Tauri marker present, which selects the
 * bearer-token transport; the web client uses the session cookie. Every flow below runs for BOTH transports and must behave identically; the one documented difference (a reload keeps a web session, a desktop
 * session lives in memory only) is asserted explicitly. Business rules are not tested here (the backend and the component tests own them): these tests prove the shipped bundle, both transports, the language and
 * direction switch, company selection, an account and a journal draft being created, permission screens, and a session expiry.
 */
const A = (k: string) => catalog.ar[k]!;
const E = (k: string) => catalog.en[k]!;
const MODES: Mode[] = ["web", "desktop"];

async function start(page: Page, mode: Mode) {
  const api = new MockApi(mode);
  await api.install(page);
  await page.goto("/");
  return api;
}
async function toEnglish(page: Page) {
  await page.locator(".auth-lang button").click();
  await expect(page.locator("html")).toHaveAttribute("lang", "en");
}
async function signIn(page: Page, password = PASSWORD) {
  await page.getByLabel(E("login.user")).fill("amal");
  await page.getByLabel(E("login.password")).fill(password);
  await page.getByRole("button", { name: E("login.submit") }).click();
}
async function enterCompany(page: Page, name: string) {
  await page.getByRole("button", { name }).click();
  await expect(page.getByTestId("company-name")).toContainText(name);
}
const go = (page: Page, hash: string) => page.evaluate((h) => { window.location.hash = h; }, hash);
const wire = (api: MockApi) => api.requests.map((r) => `${r.method} ${r.path}`);
/**
 * Readiness without a timer: a screen is ready when the requests it makes are EXACTLY its documented set, all of them answered (nothing in flight). The poll returns the moment that holds, however slow the machine
 * is, and fails with a readable diff if a request is missing or an unexpected one appears (an extra request that arrives late MAY land in the NEXT screen's window and fail there; after the LAST screen there is no next window, which the final check of the test covers). Sorted, because the reads a screen
 * starts concurrently have no defined order.
 */
async function reads(api: MockApi, from: number, expected: readonly string[]) {
  await expect.poll(() => (api.inflight === 0 ? wire(api).slice(from).sort() : ["(requests still in flight)"]), { timeout: 20_000, message: "the screen's reads" }).toEqual([...expected].sort());
}
// What each screen of the scripted flow reads, as a contract (the same on both transports).
const AFTER_SIGN_IN = ["GET /api/v1/access/me", "GET /api/v1/auth/status", "GET /api/v1/companies", "GET /api/v1/company/currencies", "GET /api/v1/company/entitlements", "GET /api/v1/company/history", "GET /api/v1/company/profile", "GET /api/v1/company/settings", "POST /api/v1/auth/login", "POST /api/v1/session/company"];
const ACCOUNTS_SCREEN = ["GET /api/v1/company/accounts", "GET /api/v1/company/currencies"];
const JOURNAL_SCREEN = ["GET /api/v1/company/accounts", "GET /api/v1/company/branches", "GET /api/v1/company/currencies", "GET /api/v1/company/journal", "GET /api/v1/company/journal/cost-centers"];
const SEQUENTIAL = ["POST /api/v1/auth/login", "GET /api/v1/companies", "POST /api/v1/session/company", "GET /api/v1/access/me"];

for (const mode of MODES) {
  test.describe(`${mode} client`, () => {
    test("login: a wrong password is refused with a message, the right one opens company selection", async ({ page }) => {
      await start(page, mode);
      await toEnglish(page);
      await signIn(page, "wrong");
      await expect(page.getByRole("alert")).toBeVisible();
      await expect(page.getByRole("heading", { name: E("companies.title") })).toHaveCount(0);
      await signIn(page);
      await expect(page.getByRole("heading", { name: E("companies.title") })).toBeVisible();
    });

    test("language switch: Arabic RTL and English LTR, on the login page and in the signed-in shell, surviving navigation", async ({ page }) => {
      await start(page, mode);
      await expect(page.locator("html")).toHaveAttribute("dir", "rtl");
      await expect(page.getByRole("button", { name: A("login.submit") })).toBeVisible();
      await toEnglish(page);
      await expect(page.locator("html")).toHaveAttribute("dir", "ltr");
      await expect(page.getByRole("button", { name: E("login.submit") })).toBeVisible();
      await signIn(page);
      await enterCompany(page, "Amal Co");
      await expect(page.locator("html")).toHaveAttribute("dir", "ltr");                                   // the choice follows the user into the shell
      await go(page, "#/company/accounts");
      await expect(page.getByRole("heading", { name: E("accounts.title"), level: 1 })).toBeVisible();
      await expect(page.locator("html")).toHaveAttribute("lang", "en");
    });

    test("company selection shows the user's companies by name in the active language and enters the chosen one", async ({ page }) => {
      await start(page, mode);
      await page.getByLabel(A("login.user")).fill("amal");
      await page.getByLabel(A("login.password")).fill(PASSWORD);
      await page.getByRole("button", { name: A("login.submit") }).click();
      await expect(page.getByRole("button", { name: "شركة الأمل" })).toBeVisible();                      // Arabic names under the Arabic UI
      await expect(page.getByRole("button", { name: "شركة محدودة" })).toBeVisible();
      await enterCompany(page, "شركة الأمل");
    });

    test("transport: web sends only the session cookie, desktop only the bearer token, every state change carries the anti-CSRF header", async ({ page }) => {
      const api = await start(page, mode);
      await toEnglish(page);
      await signIn(page);
      await enterCompany(page, "Amal Co");
      await go(page, "#/company/accounts");
      await expect(page.getByRole("heading", { name: E("accounts.title"), level: 1 })).toBeVisible();
      const authed = api.requests.filter((r) => !["/api/v1/health", "/api/v1/auth/login", "/api/v1/auth/status"].includes(r.path));
      expect(authed.length).toBeGreaterThan(2);
      for (const r of authed) expect(r.credentialsPath, `${r.method} ${r.path}`).toBe(mode === "web" ? "cookie" : "bearer");
      for (const r of api.requests.filter((x) => x.method !== "GET")) expect(r.csrfHeader, `${r.method} ${r.path}`).toBe(true);
      expect(api.requests.find((r) => r.path === "/api/v1/auth/login")!.credentialsPath).toBe("none");        // the login itself carries no credential
      if (mode === "desktop") expect(api.requests.some((r) => r.cookie)).toBe(false);                         // no cookie is ever sent by the desktop client
      const cookies = await page.context().cookies();
      if (mode === "web") expect(cookies.map((c) => c.name)).toEqual(["hasm_session"]);                     // the web session lives in the cookie jar ...
      else expect(cookies).toEqual([]);                                                                       // ... and the desktop client leaves no cookie at all
      expect(api.unhandled).toEqual([]);                                                                      // no flow depends on an endpoint the mock does not implement
    });

    test("create an account: the form validates, saves through the API and confirms", async ({ page }) => {
      const api = await start(page, mode);
      await toEnglish(page);
      await signIn(page);
      await enterCompany(page, "Amal Co");
      await go(page, "#/company/accounts");
      await page.getByRole("button", { name: E("accounts.new") }).click();
      await page.getByRole("button", { name: E("accounts.save") }).click();
      expect(api.created.accounts).toHaveLength(0);                                                           // validation stops an empty form before any request
      await page.getByLabel(new RegExp(`^${E("accounts.type")}`)).selectOption("liability");
      await page.getByLabel(E("accounts.name_en")).fill("Accounts payable");
      await page.getByRole("checkbox", { name: new RegExp(E("accounts.is_group").slice(0, 20)) }).check();
      await page.getByRole("button", { name: E("accounts.save") }).click();
      await expect(page.getByTestId("saved")).toContainText(E("accounts.created"));
      expect(api.created.accounts).toHaveLength(1);
      expect(api.created.accounts[0]).toMatchObject({ type: "liability", nameEn: "Accounts payable", isGroup: true });
    });

    test("journal draft: exact decimal text is saved as a draft through the API", async ({ page }) => {
      const api = await start(page, mode);
      await toEnglish(page);
      await signIn(page);
      await enterCompany(page, "Amal Co");
      await go(page, "#/accounting/journal");
      await page.getByRole("button", { name: E("jrn.new") }).click();
      const accounts = page.getByLabel(E("jrn.account"));
      await accounts.nth(0).selectOption("a1");
      await accounts.nth(1).selectOption("a2");
      await page.getByLabel(E("jrn.debit")).nth(0).fill("125.5");
      await page.getByLabel(E("jrn.credit")).nth(1).fill("125.5");
      await page.getByRole("button", { name: E("jrn.save_draft") }).click();
      await expect.poll(() => api.created.journal.length).toBe(1);
      expect(api.created.journal[0]).toMatchObject({ lines: [expect.objectContaining({ accountId: "a1", debit: "125.5" }), expect.objectContaining({ accountId: "a2", credit: "125.5" })] });
    });

    test("permission screens: a company without the permission gets the forbidden screen and the page never calls the API", async ({ page }) => {
      const api = await start(page, mode);
      await toEnglish(page);
      await signIn(page);
      await enterCompany(page, "Limited Co");
      await go(page, "#/company/accounts");
      await expect(page.getByTestId("forbidden")).toBeVisible();
      await go(page, "#/accounting/journal");
      await expect(page.getByTestId("forbidden")).toBeVisible();
      expect(api.requests.filter((r) => r.path.startsWith("/api/v1/company/accounts") || r.path.startsWith("/api/v1/company/journal"))).toEqual([]);
    });

    test("session expiry: the next call returns to sign-in with the expiry notice, the saved draft is kept on the server, and signing in again finds it", async ({ page }) => {
      const api = await start(page, mode);
      await toEnglish(page);
      await signIn(page);
      await enterCompany(page, "Amal Co");
      await go(page, "#/accounting/journal");
      await page.getByRole("button", { name: E("jrn.new") }).click();
      const accounts = page.getByLabel(E("jrn.account"));
      await accounts.nth(0).selectOption("a1");
      await accounts.nth(1).selectOption("a2");
      await page.getByLabel(E("jrn.debit")).nth(0).fill("125.5");
      await page.getByLabel(E("jrn.credit")).nth(1).fill("125.5");
      await page.getByRole("button", { name: E("jrn.save_draft") }).click();
      await expect.poll(() => api.created.journal.length).toBe(1);
      await expect.poll(() => api.requests.some((r) => r.method === "GET" && r.path === "/api/v1/company/journal/j1")).toBe(true);   // the UI opened the saved entry: no unsaved edits remain, so navigation is not held by the Stay/Discard dialog
      await expect(page.getByTestId("jrn-form")).toBeVisible();
      api.expire();
      await go(page, "#/company/accounts");                                                                   // any authenticated call now meets the expired session
      await expect(page.getByTestId("notice-expired")).toBeVisible();
      await expect(page.getByRole("heading", { name: E("login.title") })).toBeVisible();
      await signIn(page);
      await enterCompany(page, "Amal Co");
      await go(page, "#/accounting/journal");
      await expect(page.getByTestId("saved").or(page.getByRole("button", { name: E("jrn.new") }))).toBeVisible();
      await expect.poll(async () => (await page.content()).includes("125.5")).toBe(true);                     // the draft is still listed after re-login
      expect(api.created.journal).toHaveLength(1);                                                            // nothing was lost or duplicated
    });

    test("session expiry with UNSAVED input (D-236): the shell is hidden behind the sign-in-again dialog, the same user gets the input back in memory, and the save then succeeds", async ({ page }) => {
      const api = await start(page, mode);
      await toEnglish(page);
      await signIn(page);
      await enterCompany(page, "Amal Co");
      await go(page, "#/accounting/journal");
      await page.getByRole("button", { name: E("jrn.new") }).click();
      const accounts = page.getByLabel(E("jrn.account"));
      await accounts.nth(0).selectOption("a1");
      await accounts.nth(1).selectOption("a2");
      await page.getByLabel(E("jrn.debit")).nth(0).fill("777.25");
      await page.getByLabel(E("jrn.credit")).nth(1).fill("777.25");
      api.expire();                                                                                           // the session ends while the entry is unsaved
      await page.getByRole("button", { name: E("jrn.save_draft") }).click();                                  // the save meets the expired session (401)
      const dialog = page.getByTestId("reauth-dialog");
      await expect(dialog).toBeVisible();
      await expect(dialog).toContainText(E("reauth.message"));
      await expect(page.getByTestId("shell-root")).toBeHidden();                                              // not rendered ...
      await expect(page.getByTestId("shell-root")).toHaveAttribute("inert", "");                              // ... and inert
      await expect(page.getByTestId("jrn-form")).toBeHidden();
      await expect(dialog).not.toContainText("777.25");
      await expect(dialog).not.toContainText("Amal");                                                         // no user, no company
      expect(api.created.journal).toHaveLength(0);
      // Nothing was written to web storage or to a cookie readable by scripts (the session cookie is HttpOnly), in either client.
      const persisted = await page.evaluate(() => JSON.stringify([{ ...localStorage }, { ...sessionStorage }, document.cookie]));
      expect(persisted).not.toContain("777.25");
      // Printing shows the dialog only.
      await page.emulateMedia({ media: "print" });
      await expect(page.getByTestId("shell-root")).toBeHidden();
      await page.emulateMedia({ media: "screen" });
      // A wrong password keeps the dialog and the hidden input; then the same user signs in again.
      const before = api.requests.length;
      await signIn(page, "wrong-password");
      await expect(dialog).toContainText(E("error.invalid_credentials"));
      await expect(page.getByTestId("shell-root")).toBeHidden();
      await signIn(page);
      await expect(dialog).toBeHidden();
      await expect(page.getByTestId("jrn-form")).toBeVisible();
      await expect(page.getByLabel(E("jrn.debit")).nth(0)).toHaveValue("777.25");                             // restored from memory
      await expect(page.getByLabel(E("jrn.credit")).nth(1)).toHaveValue("777.25");
      // While the dialog was open the hidden shell sent nothing: the only requests are the two sign-ins, the company selection and the access answer.
      await expect.poll(() => (api.inflight === 0 ? wire(api).slice(before) : ["(in flight)"])).toEqual(["POST /api/v1/auth/login", "POST /api/v1/auth/login", "POST /api/v1/session/company", "GET /api/v1/access/me"]);
      expect(api.created.journal).toHaveLength(0);                                                            // nothing was re-submitted by itself
      await page.getByRole("button", { name: E("jrn.save_draft") }).click();
      await expect.poll(() => api.created.journal.length).toBe(1);                                            // the user's own save goes through on the new session
      expect(new Set(api.requests.slice(before).map((r) => r.credentialsPath).filter((c) => c !== "none"))).toEqual(new Set([mode === "web" ? "cookie" : "bearer"]));
    });

    test("session expiry with unsaved input, then 'discard and sign out': the input is gone and the normal sign-in page is shown", async ({ page }) => {
      const api = await start(page, mode);
      await toEnglish(page);
      await signIn(page);
      await enterCompany(page, "Amal Co");
      await go(page, "#/accounting/journal");
      await page.getByRole("button", { name: E("jrn.new") }).click();
      const accounts = page.getByLabel(E("jrn.account"));
      await accounts.nth(0).selectOption("a1");
      await accounts.nth(1).selectOption("a2");
      await page.getByLabel(E("jrn.debit")).nth(0).fill("888.5");
      await page.getByLabel(E("jrn.credit")).nth(1).fill("888.5");
      api.expire();
      await page.getByRole("button", { name: E("jrn.save_draft") }).click();
      await expect(page.getByTestId("reauth-dialog")).toBeVisible();
      await page.getByTestId("reauth-discard").click();
      await expect(page.getByTestId("notice-logged-out")).toBeVisible();
      await expect(page.getByTestId("shell-root")).toHaveCount(0);
      expect(await page.content()).not.toContain("888.5");
      await signIn(page);
      await enterCompany(page, "Amal Co");
      expect(await page.content()).not.toContain("888.5");
      expect(api.created.journal).toHaveLength(0);
    });

    test("logout: returns to sign-in with the notice, clears the web cookie, forgets the desktop token, and the next call carries no credential", async ({ page }) => {
      const api = await start(page, mode);
      await toEnglish(page);
      await signIn(page);
      await enterCompany(page, "Amal Co");
      if (mode === "web") expect((await page.context().cookies()).map((c) => c.name)).toEqual(["hasm_session"]);
      await page.getByRole("button", { name: E("nav.logout") }).click();
      await expect(page.getByTestId("notice-logged-out")).toBeVisible();
      await expect(page.getByRole("button", { name: E("login.submit") })).toBeVisible();
      const logout = api.requests.find((r) => r.path === "/api/v1/auth/logout")!;
      expect([logout.method, logout.csrfHeader, logout.credentialsPath]).toEqual(["POST", true, mode === "web" ? "cookie" : "bearer"]);   // the logout itself is authenticated and carries the anti-CSRF header
      expect(api.signedIn).toBe(false);
      if (mode === "web") expect(await page.context().cookies()).toEqual([]);                                 // the cookie was cleared
      const after = api.requests.length;
      await page.getByLabel(E("login.user")).fill("amal");
      await page.getByLabel(E("login.password")).fill(PASSWORD);
      await page.getByRole("button", { name: E("login.submit") }).click();
      await expect(page.getByRole("heading", { name: E("companies.title") })).toBeVisible();
      const next = api.requests.slice(after).find((r) => r.path === "/api/v1/auth/login")!;
      expect(next.credentialsPath).toBe("none");                                                              // the next sign-in starts from nothing: no cookie, no bearer
      expect(api.requests.slice(after).filter((r) => r.path !== "/api/v1/auth/login").every((r) => r.credentialsPath !== "none" || r.path === "/api/v1/auth/status")).toBe(true);
    });

    test("reload: the web session survives (cookie), the desktop session asks for the password again (token kept in memory only)", async ({ page }) => {
      await start(page, mode);
      await toEnglish(page);
      await signIn(page);
      await enterCompany(page, "Amal Co");
      await page.reload();
      if (mode === "web") await expect(page.getByTestId("company-name")).toContainText("Amal Co");
      else await expect(page.getByRole("button", { name: E("login.submit") })).toBeVisible();
      expect(await page.evaluate(() => JSON.stringify({ ...localStorage, ...sessionStorage }))).not.toMatch(/tok-/);   // a token is never written to browser storage
      expect(await page.evaluate(async () => ((await indexedDB.databases?.()) ?? []).length)).toBe(0);                  // nor to IndexedDB
    });
  });
}

test("parity: the same scripted flow sends the same requests on both transports (sequential steps in order, concurrent reads as a multiset per screen)", async ({ browser }) => {
  // The sign-in steps are sequential by design and must appear in exactly this order. The reads a screen starts concurrently have no defined order, so each screen's reads are compared as a sorted multiset against its
  // documented set once they are all answered (see `reads`: a condition, not a timer). The screens are separated by those points, so the ORDER of the screens is still compared exactly. No retries, no sleeps.
  const stages: Record<Mode, string[][]> = { web: [], desktop: [] };
  const sequential: Record<Mode, string[]> = { web: [], desktop: [] };
  for (const mode of MODES) {
    const context = await browser.newContext();
    const page = await context.newPage();
    const api = await start(page, mode);
    await toEnglish(page);
    await signIn(page);
    await enterCompany(page, "Amal Co");
    let mark = 0;
    const take = () => { const part = wire(api).slice(mark).sort(); mark = api.requests.length; return part; };
    await reads(api, mark, AFTER_SIGN_IN);
    stages[mode].push(take());
    await go(page, "#/company/accounts");
    await expect(page.getByRole("heading", { name: E("accounts.title"), level: 1 })).toBeVisible();
    await reads(api, mark, ACCOUNTS_SCREEN);
    stages[mode].push(take());
    await go(page, "#/accounting/journal");
    await expect(page.getByRole("button", { name: E("jrn.new") })).toBeVisible();
    await reads(api, mark, JOURNAL_SCREEN);
    stages[mode].push(take());
    // After the last screen is ready no further request may follow: the browser reports its network idle (an observable event, not a sleep), and the count and the in-flight number must not have moved.
    const finished = api.requests.length;
    await page.waitForLoadState("networkidle");
    expect(api.requests.length, `a request arrived after the last screen was ready (${mode})`).toBe(finished);
    expect(api.inflight).toBe(0);
    sequential[mode] = wire(api).filter((r) => SEQUENTIAL.includes(r));
    await context.close();
  }
  expect(sequential.web).toEqual(SEQUENTIAL);                                                                 // the sign-in sequence, exactly and in order
  expect(sequential.desktop).toEqual(SEQUENTIAL);
  expect(stages.desktop).toEqual(stages.web);                                                                 // every screen read the same endpoints on both transports
  expect(stages.web.flat().length).toBeGreaterThan(5);
});
