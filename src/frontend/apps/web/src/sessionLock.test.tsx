import { ApiError, createSessionGate, type ApiClient, type SessionGate } from "@hasm/api-client";
import { I18nProvider } from "@hasm/ui";
import { act, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { readFileSync } from "node:fs";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { App } from "./App";
import { AuthProvider, createSessionBridge, useAuth } from "./auth";
import { catalog } from "./messages";
import { createStepUpBridge } from "./stepUp";
import { apiError, signedIn, USER } from "./test-utils";

/**
 * Owner decision D-236 (SESSION-EXPIRY-UNSAVED): unsubmitted input is kept IN MEMORY ONLY while the session is expired, hidden from whoever is not signed in, restored
 * only after the SAME user signs in again, cleared on sign-out or an identity change, and never stored. One implementation for the browser and the desktop app; the
 * tests run the shared application with the same fake client the other screen tests use.
 */
const stored = {
  nameAr: "شركة الأمل", nameEn: null, legalName: null, commercialRegistrationNo: "CR-1/2", taxRegistrationNo: "VAT 99", countryCode: "SA",
  addressLine1: null, addressLine2: null, city: "Riyadh", region: null, postalCode: null, phone: null, email: null, website: null,
};
const SECRET = "Jeddah-unsaved-7Q";                       // the unsaved input the tests look for (and must not find where it must not be)
const OTHER = { id: "u2", login: "badr", displayName: "Badr", isIdentityAdministrator: false };

const go = (hash: string) => { window.location.hash = hash; fireEvent(window, new HashChangeEvent("hashchange")); };

/** A gate that records, at every open(), whether a shell is in the document; `log` reads e.g. ["close", "open shell=false"]. */
function recordingGate(): { gate: SessionGate; log: string[] } {
  const inner = createSessionGate();
  const log: string[] = [];
  return { log, gate: { isOpen: inner.isOpen, close: () => { log.push("close"); inner.close(); }, open: () => { log.push(`open shell=${document.querySelector('[data-testid="shell-root"]') !== null}`); inner.open(); } } };
}

interface Started { client: ApiClient; bridge: ReturnType<typeof createSessionBridge>; step: ReturnType<typeof createStepUpBridge>; log: string[]; gate: SessionGate }

/** Signed in, on the company profile, with unsaved input in "city". The save that would send it is answered 401 (the session has ended). */
async function startDirty(overrides: Partial<Record<keyof ApiClient, unknown>> = {}, permissions?: string[]): Promise<Started> {
  const client = signedIn({
    getProfile: vi.fn().mockResolvedValue({ profile: stored, revision: 3, updatedAtUtc: null, projection: "synced" }),
    putProfile: vi.fn().mockRejectedValue(apiError(401, "unauthenticated")),
    ...overrides,
  }, permissions);
  const bridge = createSessionBridge();
  const step = createStepUpBridge();
  const { gate, log } = recordingGate();
  render(<I18nProvider catalog={catalog}><App client={client} bridge={bridge} stepUpBridge={step} gate={gate} /></I18nProvider>);
  await screen.findByTestId("company-name");
  go("#/company/profile");
  const city = await screen.findByLabelText("المدينة");
  await userEvent.clear(city);
  await userEvent.type(city, SECRET);
  return { client, bridge, step, log, gate };
}
async function expireBySaving(): Promise<void> {
  await userEvent.click(screen.getByRole("button", { name: "حفظ" }));
  await screen.findByTestId("reauth-dialog");
}
async function signInAgain(password = "secret-password"): Promise<void> {
  const dialog = within(screen.getByTestId("reauth-dialog"));
  await userEvent.type(dialog.getByLabelText(/اسم المستخدم/), "amal");
  await userEvent.type(dialog.getByLabelText(/كلمة المرور/), password);
  await userEvent.click(dialog.getByRole("button", { name: "دخول" }));
}
const shell = () => screen.getByTestId("shell-root");
const cityInDocument = () => document.querySelector<HTMLInputElement>('input[value="' + SECRET + '"]') ?? [...document.querySelectorAll("input")].find((i) => i.value === SECRET) ?? null;

let storageWrites: ReturnType<typeof vi.spyOn>;
beforeEach(() => {
  localStorage.clear(); sessionStorage.clear();
  window.location.hash = "";
  storageWrites = vi.spyOn(Storage.prototype, "setItem");
});
afterEach(() => { storageWrites.mockRestore(); });

/** Nothing of the unsaved input was written to web storage, and no storage write happened at all apart from the UI language. */
function expectNothingStored(): void {
  for (const call of storageWrites.mock.calls) expect(String(call[0])).toMatch(/lang|locale/i);
  for (const store of [localStorage, sessionStorage]) for (let i = 0; i < store.length; i++) expect(store.getItem(store.key(i)!) ?? "").not.toContain(SECRET);
  expect(document.cookie).not.toContain(SECRET);
}

describe("session expiry with unsaved input (D-236)", () => {
  it("locks instead of signing out: the shell stays mounted but hidden, inert and out of the accessibility tree; the dialog is the only reachable content", async () => {
    const title = document.title;
    await startDirty();
    await expireBySaving();

    expect(shell()).toHaveAttribute("hidden");
    expect(shell()).toHaveAttribute("inert");
    expect(shell()).toHaveAttribute("aria-hidden", "true");
    expect(cityInDocument()).not.toBeNull();                                          // preserved: still the live state of the mounted screen
    expect(shell().contains(cityInDocument())).toBe(true);
    expect(screen.queryByRole("textbox", { name: "المدينة" })).toBeNull();             // ...but not reachable
    expect(screen.queryByTestId("company-name")).not.toBeVisible();

    // Everything focusable is inside the dialog (nothing of the shell, no skip link, no other dialog).
    const dialog = screen.getByTestId("reauth-dialog");
    const focusable = [...document.querySelectorAll<HTMLElement>("a[href], button, input, select, textarea, [tabindex]")].filter((e) => !shell().contains(e));
    expect(focusable.length).toBeGreaterThan(0);
    for (const e of focusable) expect(dialog.contains(e)).toBe(true);
    expect(document.querySelector(".skip-link")).toBeNull();
    expect(dialog.contains(document.activeElement)).toBe(true);

    // The dialog tells nothing about what is behind it: no user, no company, no value, no section name.
    for (const hidden of [SECRET, USER.displayName, USER.login, "شركة الأمل", "Amal Co", "Riyadh"]) expect(dialog.textContent).not.toContain(hidden);
    expect(document.title).toBe(title);                                                // the title never carries screen content
    expect(document.title).not.toContain(SECRET);
    expectNothingStored();
  });

  it("is not printed and cannot be shown again by a layout rule (stylesheet assertion)", () => {
    const css = readFileSync("src/app.css", "utf8")   /* vitest runs in apps/web */.replace(/\s+/g, " ");
    expect(css).toContain(".shell-root[hidden] { display: none !important; }");
    expect(css).toMatch(/@media print \{ \.shell-root\[hidden\], \.shell-root\[hidden\] \* \{ display: none !important; visibility: hidden !important; \} \}/);
  });

  it("refuses every call of the hidden shell locally and ignores navigation while locked", async () => {
    const { client, gate } = await startDirty();
    await expireBySaving();
    expect(gate.isOpen()).toBe(false);
    const calls = (client.getProfile as ReturnType<typeof vi.fn>).mock.calls.length;
    const history = (client.getHistory as ReturnType<typeof vi.fn>).mock.calls.length;
    const address = window.location.hash;
    go("#/company/history");                                                           // a typed address / Back while locked
    await act(async () => { await Promise.resolve(); });
    expect(window.location.hash).toBe(address);                                        // put back
    expect(screen.queryByText("تجاهل التغييرات")).toBeNull();                            // no Stay / Discard question on top of the dialog
    expect((client.getHistory as ReturnType<typeof vi.fn>).mock.calls.length).toBe(history);
    expect((client.getProfile as ReturnType<typeof vi.fn>).mock.calls.length).toBe(calls);
    expect(cityInDocument()).not.toBeNull();
  });

  it("restores the input only after the SAME user signs in again, re-enters the same company, and leaves the save to the user (no automatic re-submit)", async () => {
    const { client, log } = await startDirty();
    await expireBySaving();
    const put = client.putProfile as ReturnType<typeof vi.fn>;
    expect(put).toHaveBeenCalledTimes(1);
    put.mockResolvedValue({ profile: { ...stored, city: SECRET }, revision: 4, updatedAtUtc: null, projection: "synced" });
    log.length = 0;
    await signInAgain();

    await waitFor(() => expect(screen.queryByTestId("reauth-dialog")).toBeNull());
    expect(client.login).toHaveBeenCalledWith("amal", "secret-password");
    expect(client.selectCompany).toHaveBeenLastCalledWith("c1");
    expect(shell()).not.toHaveAttribute("hidden");
    expect(shell()).not.toHaveAttribute("inert");
    expect(screen.getByLabelText("المدينة")).toHaveValue(SECRET);
    expect(log).toEqual(["open shell=true"]);                                          // opened once, after the same-user and same-company check
    expect(put).toHaveBeenCalledTimes(1);                                              // nothing was sent again by itself
    expect(document.querySelector('input[type="password"]')).toBeNull();               // the dialog's own inputs are gone with it

    await userEvent.click(screen.getByRole("button", { name: "حفظ" }));
    await waitFor(() => expect(put).toHaveBeenCalledTimes(2));
    expect(put.mock.calls[1][1]).toBe(3);                                              // the revision the form was loaded with: the server decides
    expectNothingStored();
  });

  it("leaves optimistic concurrency to the server: a save after signing in again that meets a newer revision shows the conflict and keeps the input", async () => {
    const { client } = await startDirty();
    await expireBySaving();
    (client.putProfile as ReturnType<typeof vi.fn>).mockRejectedValue(apiError(412, "revision_conflict", { currentRevision: 4 }));
    await signInAgain();
    await waitFor(() => expect(screen.queryByTestId("reauth-dialog")).toBeNull());
    await userEvent.click(screen.getByRole("button", { name: "حفظ" }));
    expect(await screen.findByTestId("conflict")).toBeInTheDocument();
    expect(screen.getByLabelText("المدينة")).toHaveValue(SECRET);
  });

  it("drops the input when a DIFFERENT user signs in: the shell is unmounted before the gate reopens and the old screen never renders for them", async () => {
    const { client, log } = await startDirty();
    await expireBySaving();
    (client.login as ReturnType<typeof vi.fn>).mockResolvedValue({ user: OTHER });
    const loads = (client.getProfile as ReturnType<typeof vi.fn>).mock.calls.length;
    log.length = 0;
    await signInAgain();

    await waitFor(() => expect(screen.getByLabelText("المدينة")).toHaveValue("Riyadh"));   // a fresh screen with the server's data
    expect(screen.queryByTestId("reauth-dialog")).toBeNull();
    expect(cityInDocument()).toBeNull();
    expect(document.body.textContent).not.toContain(SECRET);
    expect(screen.getByText(OTHER.displayName)).toBeInTheDocument();
    // Ordering: the gate was closed again, and it was opened only when no shell was in the document.
    expect(log[0]).toBe("close");
    expect(log.filter((l) => l.startsWith("open"))).toEqual(["open shell=false"]);
    expect((client.getProfile as ReturnType<typeof vi.fn>).mock.calls.length).toBe(loads + 1);   // exactly the new screen's own load
    expectNothingStored();
  });

  it("never reopens the gate for a different user while the old shell is still in the document (ordering, observed inside the sign-in calls)", async () => {
    const seen: string[] = [];
    const { client, gate } = await startDirty();
    await expireBySaving();
    const observe = (name: string) => () => { seen.push(`${name}: gate=${gate.isOpen()} shell=${document.querySelector('[data-testid="shell-root"]') !== null}`); };
    (client.login as ReturnType<typeof vi.fn>).mockImplementation(async () => { observe("login")(); return { user: OTHER }; });
    (client.listCompanies as ReturnType<typeof vi.fn>).mockImplementation(async () => { observe("listCompanies")(); return { companies: [{ id: "c1", nameAr: "شركة الأمل", nameEn: "Amal Co" }] }; });
    (client.selectCompany as ReturnType<typeof vi.fn>).mockImplementation(async () => { observe("selectCompany")(); return { companyId: "c1" }; });
    (client.accessMe as ReturnType<typeof vi.fn>).mockImplementation(async () => { observe("accessMe")(); return { companyId: "c1", grants: [{ roleId: "r", scope: "company", scopeId: null, permissions: ["company.profile.view", "company.profile.update"] }] }; });
    await signInAgain();
    await waitFor(() => expect(screen.getByLabelText("المدينة")).toHaveValue("Riyadh"));
    expect(seen).toEqual([
      "login: gate=false shell=true",              // still locked, hidden shell present, gate closed
      "listCompanies: gate=false shell=false",     // the shell is gone BEFORE anything else happens for the new user
      "selectCompany: gate=false shell=false",
      "accessMe: gate=false shell=false",          // the gate opens only after this answer
    ]);
  });

  it("drops the input when the same user can no longer enter the company (refused, or the server answers another company)", async () => {
    const { client } = await startDirty();
    await expireBySaving();
    (client.selectCompany as ReturnType<typeof vi.fn>).mockRejectedValue(apiError(403, "forbidden"));
    await signInAgain();
    expect(await screen.findByRole("heading", { name: "اختيار الشركة" })).toBeInTheDocument();
    expect(screen.queryByTestId("shell-root")).toBeNull();
    expect(cityInDocument()).toBeNull();
    expect(screen.queryByTestId("reauth-dialog")).toBeNull();
  });

  it("drops the input when the access answer of the new session is for another company", async () => {
    const { client } = await startDirty();
    await expireBySaving();
    const access = client.accessMe as ReturnType<typeof vi.fn>;
    access.mockResolvedValueOnce({ companyId: "c9", grants: [] });
    await signInAgain();
    await waitFor(() => expect(screen.queryByTestId("reauth-dialog")).toBeNull());
    await waitFor(() => expect(screen.getByLabelText("المدينة")).toHaveValue("Riyadh"));
    expect(cityInDocument()).toBeNull();
  });

  it("drops the input of a screen whose permission is gone after signing in again: the normal no-access rendering replaces it", async () => {
    const { client } = await startDirty();
    await expireBySaving();
    (client.accessMe as ReturnType<typeof vi.fn>).mockResolvedValue({ companyId: "c1", grants: [{ roleId: "r", scope: "company", scopeId: null, permissions: ["company.config.view"] }] });
    await signInAgain();
    expect(await screen.findByTestId("forbidden")).toBeInTheDocument();
    expect(screen.queryByTestId("reauth-dialog")).toBeNull();
    expect(cityInDocument()).toBeNull();
    expect(document.body.textContent).not.toContain(SECRET);
    // Nothing is reported unsaved any more: leaving needs no confirmation.
    go("#/company/history");
    await waitFor(() => expect(window.location.hash).toBe("#/company/history"));
    expect(screen.queryByText("تجاهل التغييرات")).toBeNull();
  });

  it("clears the input on the explicit 'discard and sign out', and a later sign-in of the same user starts fresh", async () => {
    const { client } = await startDirty();
    await expireBySaving();
    await userEvent.click(screen.getByTestId("reauth-discard"));
    expect(await screen.findByTestId("notice-logged-out")).toBeInTheDocument();
    expect(screen.queryByTestId("shell-root")).toBeNull();
    expect(cityInDocument()).toBeNull();
    expect(client.logout).toHaveBeenCalled();
    await userEvent.type(screen.getByLabelText(/اسم المستخدم/), "amal");
    await userEvent.type(screen.getByLabelText(/كلمة المرور/), "secret-password");
    await userEvent.click(screen.getByRole("button", { name: "دخول" }));
    await waitFor(() => expect(screen.getByLabelText("المدينة")).toHaveValue("Riyadh"));
    expect(document.body.textContent).not.toContain(SECRET);
  });

  it("clears the input when the user signs out in ANOTHER tab (cross-tab event), locked or not", async () => {
    await startDirty();
    await expireBySaving();
    const other = new BroadcastChannel("hasm-session");
    try {
      other.postMessage("signed-out");
      expect(await screen.findByTestId("notice-logged-out")).toBeInTheDocument();
      expect(screen.queryByTestId("shell-root")).toBeNull();
      expect(screen.queryByTestId("reauth-dialog")).toBeNull();
      expect(cityInDocument()).toBeNull();
    } finally { other.close(); }
  });

  it("ends a live session with unsaved input when another tab signs out, and when another tab signs in as someone else", async () => {
    const first = await startDirty();
    const other = new BroadcastChannel("hasm-session");
    try {
      other.postMessage("signed-out");
      expect(await screen.findByTestId("notice-logged-out")).toBeInTheDocument();
      expect(cityInDocument()).toBeNull();
      expect(first.gate.isOpen()).toBe(false);
    } finally { other.close(); }
  });

  it("ends the session when another tab signs in as a different user (the shared cookie changed hands)", async () => {
    const { client, gate } = await startDirty();
    await expireBySaving();
    (client.status as ReturnType<typeof vi.fn>).mockResolvedValue({ authenticated: true, user: OTHER });
    const other = new BroadcastChannel("hasm-session");
    try {
      other.postMessage("signed-in");
      await waitFor(() => expect(screen.queryByTestId("reauth-dialog")).toBeNull());
      expect(await screen.findByTestId("notice-expired")).toBeInTheDocument();
      expect(cityInDocument()).toBeNull();
      expect(gate.isOpen()).toBe(false);
    } finally { other.close(); }
  });

  it("keeps the lock when another tab signs in as the SAME user: the input comes back only through this tab's own sign-in", async () => {
    const { client } = await startDirty();
    await expireBySaving();
    (client.status as ReturnType<typeof vi.fn>).mockResolvedValue({ authenticated: true, user: USER });
    const other = new BroadcastChannel("hasm-session");
    try {
      other.postMessage("signed-in");
      await waitFor(() => expect((client.status as ReturnType<typeof vi.fn>).mock.calls.length).toBeGreaterThan(1));
      expect(screen.getByTestId("reauth-dialog")).toBeInTheDocument();
      expect(cityInDocument()).not.toBeNull();
    } finally { other.close(); }
  });

  it("runs the second factor inside the dialog, keeps the shell mounted meanwhile, and clears the password and the code on every outcome", async () => {
    const { client } = await startDirty();
    await expireBySaving();
    const login = client.login as ReturnType<typeof vi.fn>;
    login.mockRejectedValueOnce(apiError(401, "invalid_credentials"));
    await signInAgain("wrong");
    const dialog = within(screen.getByTestId("reauth-dialog"));
    expect(await dialog.findByText("اسم المستخدم أو كلمة المرور غير صحيحة.")).toBeInTheDocument();
    expect(dialog.getByLabelText(/كلمة المرور/)).toHaveValue("");                       // a refused password is not kept
    expect(cityInDocument()).not.toBeNull();

    login.mockResolvedValue({ step: { kind: "mfa_required", challenge: "ch-1" } });
    (client as unknown as { verifyMfa: unknown }).verifyMfa = vi.fn().mockRejectedValueOnce(apiError(401, "invalid_credentials")).mockResolvedValue({ user: USER });
    await userEvent.type(dialog.getByLabelText(/كلمة المرور/), "secret-password");
    await userEvent.click(dialog.getByRole("button", { name: "دخول" }));
    const code = await dialog.findByLabelText(/رمز التحقق/);
    expect(document.querySelector('input[type="password"]')).toBeNull();                // the password field is gone once the step changes
    expect(shell()).toHaveAttribute("hidden");                                          // still locked: the shell did not unmount for the second step
    expect(cityInDocument()).not.toBeNull();
    await userEvent.type(code, "111111");
    await userEvent.click(dialog.getByRole("button", { name: "تحقق" }));
    await waitFor(() => expect(dialog.getByLabelText(/رمز التحقق/)).toHaveValue(""));    // a refused code is not kept
    await userEvent.type(dialog.getByLabelText(/رمز التحقق/), "222222");
    await userEvent.click(dialog.getByRole("button", { name: "تحقق" }));
    await waitFor(() => expect(screen.queryByTestId("reauth-dialog")).toBeNull());
    expect(screen.getByLabelText("المدينة")).toHaveValue(SECRET);
    expect(document.querySelector('input[autocomplete="one-time-code"]')).toBeNull();
    expect(document.querySelector('input[type="password"]')).toBeNull();
  }, 20_000);   // the longest flow of the file (three sign-in attempts typed key by key): a wider limit for a loaded machine, the assertions are unchanged

  it("withdraws an open step-up dialog when the session ends (its request is reported as cancelled)", async () => {
    const { bridge, step } = await startDirty();
    let asked: Promise<boolean> = Promise.resolve(true);
    act(() => { asked = step.request(); });
    expect(await screen.findByRole("dialog")).toBeInTheDocument();
    act(() => bridge.fire());                                                           // the api client reports a 401
    await screen.findByTestId("reauth-dialog");
    await expect(asked).resolves.toBe(false);
    expect(screen.getAllByRole("dialog")).toHaveLength(1);                              // only the sign-in-again dialog
  });

  it("works the same in English (LTR): the dialog is translated and the input survives the language switch and the sign-in", async () => {
    await startDirty();
    await expireBySaving();
    await userEvent.click(within(screen.getByTestId("reauth-dialog")).getByRole("button", { name: /English/i }));
    expect(document.documentElement.dir).toBe("ltr");
    const dialog = within(screen.getByTestId("reauth-dialog"));
    expect(dialog.getByTestId("reauth-message")).toHaveTextContent("Your session ended before your input was saved.");
    expect(dialog.getByRole("button", { name: "Discard unsaved input and sign out" })).toBeInTheDocument();
    await userEvent.type(dialog.getByLabelText(/User name|Username|Login/i), "amal");
    await userEvent.type(dialog.getByLabelText(/Password/i), "secret-password");
    await userEvent.click(dialog.getByRole("button", { name: /Sign in/i }));
    await waitFor(() => expect(screen.queryByTestId("reauth-dialog")).toBeNull());
    expect(screen.getByLabelText("City")).toHaveValue(SECRET);
  });

  it("changes nothing when nothing is unsaved: the session ends on the normal sign-in page, as before", async () => {
    const client = signedIn({ getProfile: vi.fn().mockRejectedValue(apiError(401, "unauthenticated")) });
    render(<I18nProvider catalog={catalog}><App client={client} /></I18nProvider>);
    await screen.findByTestId("company-name");
    go("#/company/profile");
    expect(await screen.findByTestId("notice-expired")).toBeInTheDocument();
    expect(screen.queryByTestId("reauth-dialog")).toBeNull();
    expect(screen.queryByTestId("shell-root")).toBeNull();
  });

  // ---- review of PR 196 (FH): M-1 to M-4, L-2
  it("names the dialog after its OWN title, not after the hidden screen, and shares no element id with the hidden shell (Arabic, English, second step)", async () => {
    const { client } = await startDirty();
    await expireBySaving();
    const duplicateIds = () => { const ids = [...document.querySelectorAll("[id]")].map((e) => e.id); return ids.filter((id, i) => ids.indexOf(id) !== i); };
    expect(screen.getByTestId("reauth-dialog")).toHaveAccessibleName("تسجيل الدخول");
    expect(screen.getByRole("dialog", { name: "تسجيل الدخول" })).toBe(screen.getByTestId("reauth-dialog"));
    expect(shell().querySelector("#page-title")).not.toBeNull();                        // the hidden page still has its own title id ...
    expect(screen.getByTestId("reauth-dialog").querySelector("#page-title")).toBeNull(); // ... and the dialog does not use it
    expect(duplicateIds()).toEqual([]);
    for (const label of [/اسم المستخدم/, /كلمة المرور/]) expect(screen.getByTestId("reauth-dialog").contains(screen.getByLabelText(label))).toBe(true);

    await userEvent.click(within(screen.getByTestId("reauth-dialog")).getByRole("button", { name: /English/i }));
    expect(screen.getByTestId("reauth-dialog")).toHaveAccessibleName("Sign in");
    expect(duplicateIds()).toEqual([]);

    (client.login as ReturnType<typeof vi.fn>).mockResolvedValue({ step: { kind: "mfa_required", challenge: "ch-1" } });
    const dialog = within(screen.getByTestId("reauth-dialog"));
    await userEvent.type(dialog.getByLabelText(/User name|Username|Login/i), "amal");
    await userEvent.type(dialog.getByLabelText(/Password/i), "secret-password");
    await userEvent.click(dialog.getByRole("button", { name: /Sign in/i }));
    await dialog.findByLabelText(/Verification code/);
    expect(screen.getByTestId("reauth-dialog")).toHaveAccessibleName("Two-step verification");
    expect(duplicateIds()).toEqual([]);
  });

  it("decides by the user ID the server returns, never by the name: the same login name under another id is dropped", async () => {
    const { client } = await startDirty();
    await expireBySaving();
    (client.login as ReturnType<typeof vi.fn>).mockResolvedValue({ user: { ...USER, id: "u1-other" } });   // same login and display name, another account
    await signInAgain();
    await waitFor(() => expect(screen.getByLabelText("المدينة")).toHaveValue("Riyadh"));
    expect(screen.queryByTestId("reauth-dialog")).toBeNull();
    expect(cityInDocument()).toBeNull();
    expect(document.body.textContent).not.toContain(SECRET);
  });

  it("decides by the user ID the server returns, never by the name: the same id under a changed login and display name is restored", async () => {
    const { client } = await startDirty();
    await expireBySaving();
    (client.login as ReturnType<typeof vi.fn>).mockResolvedValue({ user: { ...USER, login: "amal.renamed", displayName: "Amal R." } });
    await signInAgain();
    await waitFor(() => expect(screen.queryByTestId("reauth-dialog")).toBeNull());
    expect(screen.getByLabelText("المدينة")).toHaveValue(SECRET);
    expect(screen.getByText("Amal R.")).toBeInTheDocument();
  });

  for (const [what, failure] of [["a network error", new Error("network down")], ["a server error (503)", apiError(503, "unavailable")], ["a throttle (429)", apiError(429, "too_many_requests")]] as const) {
    it(`keeps the lock and the input when re-entering the company fails with ${what}, says so, and restores on the next sign-in`, async () => {
      const { client, gate } = await startDirty();
      await expireBySaving();
      const select = client.selectCompany as ReturnType<typeof vi.fn>;
      select.mockRejectedValueOnce(failure);
      await signInAgain();
      expect(await screen.findByTestId("notice-resume-failed")).toBeInTheDocument();
      expect(screen.getByTestId("reauth-dialog")).toBeInTheDocument();
      expect(shell()).toHaveAttribute("hidden");
      expect(cityInDocument()).not.toBeNull();                                          // the same user's input is NOT destroyed by a transient failure
      expect(gate.isOpen()).toBe(false);
      expect(within(screen.getByTestId("reauth-dialog")).getByLabelText(/كلمة المرور/)).toHaveValue("");
      await signInAgain();                                                              // the retry
      await waitFor(() => expect(screen.queryByTestId("reauth-dialog")).toBeNull());
      expect(screen.getByLabelText("المدينة")).toHaveValue(SECRET);
      expect(screen.queryByTestId("notice-resume-failed")).toBeNull();
    });
  }

  it("keeps the lock when the access answer fails with a server error after the company was selected", async () => {
    const { client } = await startDirty();
    await expireBySaving();
    (client.accessMe as ReturnType<typeof vi.fn>).mockRejectedValueOnce(apiError(500, "server_error"));
    await signInAgain();
    expect(await screen.findByTestId("notice-resume-failed")).toBeInTheDocument();
    expect(cityInDocument()).not.toBeNull();
  });

  it("leaves the second step when the company cannot be reached after a correct code (the challenge is spent) and still keeps the input", async () => {
    const { client } = await startDirty();
    await expireBySaving();
    (client.login as ReturnType<typeof vi.fn>).mockResolvedValueOnce({ step: { kind: "mfa_required", challenge: "ch-1" } });
    (client as unknown as { verifyMfa: unknown }).verifyMfa = vi.fn().mockResolvedValue({ user: USER });
    (client.selectCompany as ReturnType<typeof vi.fn>).mockRejectedValueOnce(new Error("network down"));
    await signInAgain();
    const dialog = within(screen.getByTestId("reauth-dialog"));
    await userEvent.type(await dialog.findByLabelText(/رمز التحقق/), "123456");
    await userEvent.click(dialog.getByRole("button", { name: "تحقق" }));
    expect(await screen.findByTestId("notice-resume-failed")).toBeInTheDocument();
    expect(dialog.getByLabelText(/كلمة المرور/)).toBeInTheDocument();                   // back on the password form, not stuck on a spent code step
    expect(document.querySelector('input[autocomplete="one-time-code"]')).toBeNull();
    expect(cityInDocument()).not.toBeNull();
  });

  it("never strands the application on the loading screen: a different user whose company list cannot be read gets the unreachable page with its retry", async () => {
    const { client } = await startDirty();
    await expireBySaving();
    (client.login as ReturnType<typeof vi.fn>).mockResolvedValue({ user: OTHER });
    (client.listCompanies as ReturnType<typeof vi.fn>).mockRejectedValue(new Error("network down"));
    await signInAgain();
    expect(await screen.findByTestId("unreachable")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "إعادة المحاولة" })).toBeInTheDocument();
    expect(screen.queryByTestId("reauth-dialog")).toBeNull();
    expect(cityInDocument()).toBeNull();                                                // dropped all the same: it was another user
  });

  it("never strands the application on the loading screen: the same user refused the company, then no company list", async () => {
    const { client } = await startDirty();
    await expireBySaving();
    (client.selectCompany as ReturnType<typeof vi.fn>).mockRejectedValue(apiError(403, "forbidden"));
    (client.listCompanies as ReturnType<typeof vi.fn>).mockRejectedValue(apiError(503, "unavailable"));
    await signInAgain();
    expect(await screen.findByTestId("unreachable")).toBeInTheDocument();
    expect(cityInDocument()).toBeNull();
  });

  it("a call refused by the closed gate is never taken for a session end: Change company with a slow company list keeps the user signed in (and the tab can still lock later)", async () => {
    let release: (v: { companies: { id: string; nameAr: string; nameEn: string }[] }) => void = () => undefined;
    const companies = { companies: [{ id: "c1", nameAr: "شركة الأمل", nameEn: "Amal Co" }, { id: "c2", nameAr: "مؤسسة النور", nameEn: "Noor Est" }] };
    const listCompanies = vi.fn().mockResolvedValueOnce(companies).mockImplementationOnce(() => new Promise((resolve) => { release = resolve; })).mockResolvedValue(companies);
    const client = signedIn({
      listCompanies,
      getProfile: vi.fn().mockResolvedValue({ profile: stored, revision: 3, updatedAtUtc: null, projection: "synced" }),
      putProfile: vi.fn().mockRejectedValue(apiError(401, "unauthenticated")),
    });
    const { gate } = recordingGate();
    render(<I18nProvider catalog={catalog}><App client={client} gate={gate} /></I18nProvider>);
    await screen.findByTestId("company-name");
    go("#/account/security");                                                           // this screen reads the session status whenever the session value changes
    await screen.findByRole("heading", { name: "الأمان", level: 1 });
    await userEvent.click(screen.getByRole("button", { name: "تغيير الشركة" }));
    expect(await screen.findByRole("heading", { name: "اختيار الشركة" })).toBeInTheDocument();   // at once, while the fresh list is still loading
    expect(screen.queryByTestId("shell-root")).toBeNull();                              // no mounted shell behind the closed gate
    expect(gate.isOpen()).toBe(false);
    expect(screen.queryByTestId("notice-expired")).toBeNull();
    await act(async () => { release(companies); await Promise.resolve(); });
    expect(screen.queryByTestId("notice-expired")).toBeNull();
    expect(client.logout).not.toHaveBeenCalled();
    await userEvent.click(screen.getByRole("button", { name: /شركة الأمل/ }));
    await screen.findByTestId("company-name");
    expect(screen.getByText(USER.displayName)).toBeInTheDocument();                     // still the signed-in user
    expect(gate.isOpen()).toBe(true);
    // ... and the lock for unsaved input still arms in this tab.
    go("#/company/profile");
    const city = await screen.findByLabelText("المدينة");
    await userEvent.clear(city);
    await userEvent.type(city, SECRET);
    await userEvent.click(screen.getByRole("button", { name: "حفظ" }));
    expect(await screen.findByTestId("reauth-dialog")).toBeInTheDocument();
  });

  it("handleError swallows the gate's own refusal in every state and changes nothing", async () => {
    let handle: (e: unknown) => boolean = () => false;
    function Probe() { const a = useAuth(); handle = a.handleError; return <span data-testid="probe-phase">{a.phase}:{String(a.locked)}:{a.user?.id ?? "-"}</span>; }
    const gate = createSessionGate();
    render(<I18nProvider catalog={catalog}><AuthProvider api={signedIn()} gate={gate}><Probe /></AuthProvider></I18nProvider>);
    await waitFor(() => expect(screen.getByTestId("probe-phase")).toHaveTextContent("ready:false:u1"));
    let handled = false;
    act(() => { handled = handle(new ApiError(401, "session_locked")); });
    expect(handled).toBe(true);
    expect(screen.getByTestId("probe-phase")).toHaveTextContent("ready:false:u1");      // not signed out, not locked
    expect(gate.isOpen()).toBe(true);
    act(() => { handled = handle(new ApiError(401, "unauthenticated")); });             // a real 401 still ends the session
    expect(handled).toBe(true);
    await waitFor(() => expect(screen.getByTestId("probe-phase")).toHaveTextContent("anonymous:false:-"));
    expect(gate.isOpen()).toBe(false);
  });

  it("starts with a CLOSED gate and opens it only for a confirmed session with a company (an application built on a gate that starts open fails here)", async () => {
    const gate = createSessionGate();
    let answer: (v: unknown) => void = () => undefined;
    const client = signedIn({ status: vi.fn().mockImplementation(() => new Promise((resolve) => { answer = resolve; })) });
    render(<I18nProvider catalog={catalog}><App client={client} gate={gate} /></I18nProvider>);
    expect(gate.isOpen()).toBe(false);                                                  // nothing confirmed yet
    await act(async () => { answer({ authenticated: false }); await Promise.resolve(); });
    await screen.findByRole("heading", { name: "تسجيل الدخول" });
    expect(gate.isOpen()).toBe(false);                                                  // the sign-in page: still closed
    await userEvent.type(screen.getByLabelText(/اسم المستخدم/), "amal");
    await userEvent.type(screen.getByLabelText(/كلمة المرور/), "secret-password");
    await userEvent.click(screen.getByRole("button", { name: "دخول" }));
    await screen.findByTestId("company-name");
    expect(gate.isOpen()).toBe(true);
    await userEvent.click(screen.getByRole("button", { name: "تسجيل الخروج" }));
    await screen.findByTestId("notice-logged-out");
    expect(gate.isOpen()).toBe(false);
  });

  it("treats a call refused by the closed gate as nothing new: no second dialog, no sign-out", async () => {
    const { client } = await startDirty();
    await expireBySaving();
    // The hidden screen tries again (a retry, a late effect): refused locally with 401 session_locked.
    await expect(client.getHealth()).resolves.toBeDefined();                            // the raw client is untouched (the provider signs in through it)
    expect(screen.getAllByTestId("reauth-dialog")).toHaveLength(1);
    expect(new ApiError(401, "session_locked").status).toBe(401);
  });
});
