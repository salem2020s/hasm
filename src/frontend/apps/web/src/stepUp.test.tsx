import { createApiClient } from "@hasm/api-client";
import { I18nProvider } from "@hasm/ui";
import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { App } from "./App";
import { createSessionBridge } from "./auth";
import { catalog } from "./messages";
import { createStepUpBridge } from "./stepUp";

/**
 * MFA step-up (Phase 4.1, shared by Web and Desktop): the REAL api client talks to a scripted server through the whole app. A stale second-factor proof (401 mfa_step_up_required on an authenticated call) opens ONE dialog,
 * a good code repeats the original request once, a cancel leaves the screen and its input intact, and no other 401 is confused with it. The code is never stored. The action here is the chart-of-accounts create form: the
 * mechanism is in the api client and does not depend on which call is refused (the server enforces it on the role grant and the user-state change).
 */
const A = (k: string) => catalog.ar[k]!;
const E = (k: string) => catalog.en[k]!;
beforeEach(() => { localStorage.clear(); sessionStorage.clear(); window.location.hash = ""; });

const json = (body: unknown, status = 200, headers: Record<string, string> = {}) => new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json", ...headers } });
const account = { id: "n1", code: "2", nameAr: "الخصوم", nameEn: null, parentId: null, type: "liability", nature: "credit", statement: "balance_sheet", isGroup: true, isActive: true, postable: false, hasChildren: false, currencyPolicy: "base", currencyCode: null, template: null, revision: 1, createdAtUtc: "2026-01-01T00:00:00Z", updatedAtUtc: "2026-01-01T00:00:00Z" };
const PERMS = ["company.accounts.view", "company.accounts.create"];

type Reply = Response | (() => Response);
interface Script { create: Reply[]; stepUp: Reply[] }

function setup(script: Script) {
  const calls: { method: string; path: string; body: unknown; headers: Record<string, string> }[] = [];
  const queue = { create: [...script.create], stepUp: [...script.stepUp] };
  const next = (q: Reply[]) => { const r = q.shift(); if (!r) throw new Error("the script ran out of answers"); return typeof r === "function" ? r() : r; };
  const fetchMock = vi.fn().mockImplementation(async (url: string, init: RequestInit = {}) => {
    const path = new URL(url).pathname;
    const method = init.method ?? "GET";
    calls.push({ method, path, body: typeof init.body === "string" ? JSON.parse(init.body) : undefined, headers: init.headers as Record<string, string> });
    if (path === "/api/v1/auth/login") return json({ user: { id: "u1", login: "amal", displayName: "Amal", isIdentityAdministrator: false } });   // signing in again after the session ended (D-236)
    if (path === "/api/v1/auth/status") return json({ authenticated: true, user: { id: "u1", login: "amal", displayName: "Amal", isIdentityAdministrator: false } });
    if (path === "/api/v1/companies") return json({ companies: [{ id: "c1", nameAr: "شركة الأمل", nameEn: "Amal Co" }] });
    if (path === "/api/v1/session/company") return json({ companyId: "c1" });
    if (path === "/api/v1/access/me") return json({ companyId: "c1", grants: [{ roleId: "r", scope: "company", scopeId: null, permissions: PERMS }] });
    if (path === "/api/v1/company/currencies") return json({ currencies: [], baseCurrency: null, baseCurrencySelected: false, baseCurrencyLock: { locked: false, checksImplemented: false, unimplementedDomains: [] } });
    if (path === "/api/v1/company/accounts" && method === "GET") return json({ accounts: [], settings: { segmentWidth: 2, autoCodes: true, revision: 0, configured: false }, templates: [], usageChecksImplemented: false, can: { create: true, update: false, activate: false, recode: false, restructure: false, configure: false } });
    if (path === "/api/v1/company/accounts" && method === "POST") return next(queue.create);
    if (path === "/api/v1/auth/mfa/step-up") return next(queue.stepUp);
    return json({ error: "not_found" }, 404);
  });
  const session = createSessionBridge();
  const step = createStepUpBridge();
  const client = createApiClient("https://x", fetchMock as unknown as typeof fetch, { client: "web", onUnauthenticated: session.fire, stepUp: step.request });
  render(<I18nProvider catalog={catalog}><App client={client} bridge={session} stepUpBridge={step} /></I18nProvider>);
  return { calls, fetchMock, step, posts: () => calls.filter((c) => c.method === "POST" && c.path === "/api/v1/company/accounts"), stepUps: () => calls.filter((c) => c.path === "/api/v1/auth/mfa/step-up") };
}

const STALE = () => json({ error: "mfa_step_up_required" }, 401);
const CREATED = () => json(account, 201);

async function openAndFill() {
  await screen.findByTestId("company-name");
  window.location.hash = "#/company/accounts";
  fireEvent(window, new HashChangeEvent("hashchange"));
  await userEvent.click(await screen.findByRole("button", { name: "حساب جديد" }));
  await userEvent.selectOptions(screen.getByLabelText(/^النوع\s*\*?$/), "liability");
  await userEvent.type(screen.getByLabelText("الاسم بالعربية"), "الخصوم");
  await userEvent.click(screen.getByRole("checkbox", { name: /حساب رئيسي/ }));
  await userEvent.click(screen.getByRole("button", { name: "حفظ الحساب" }));
}
const dialog = () => screen.findByRole("dialog");

describe("MFA step-up dialog", () => {
  it("a stale proof opens the dialog, a good code repeats the SAME request once and the action completes without ending the session", async () => {
    const s = setup({ create: [STALE, CREATED], stepUp: [() => json({ verifiedAtUtc: "2026-11-21T10:00:00Z" })] });
    await openAndFill();
    const d = await dialog();
    expect(within(d).getByText(A("mfa.step_up.title"))).toBeInTheDocument();
    expect(within(d).getByText(A("mfa.step_up.message"))).toBeInTheDocument();
    await waitFor(() => expect(within(d).getByLabelText(new RegExp(A("mfa.code")))).toHaveFocus());                     // the code field has the focus, not Cancel
    await userEvent.type(within(d).getByLabelText(new RegExp(A("mfa.code"))), "123456");
    await userEvent.click(within(d).getByRole("button", { name: A("mfa.verify") }));
    expect(await screen.findByTestId("saved")).toHaveTextContent(A("accounts.created"));
    expect(s.stepUps()).toHaveLength(1);
    expect(s.stepUps()[0]!.body).toEqual({ code: "123456" });
    expect(s.stepUps()[0]!.headers["X-Hasm-Request"]).toBe("1");                                                          // like every state-changing call
    expect(s.posts()).toHaveLength(2);                                                                                    // refused once, repeated once
    expect(s.posts()[1]!.body).toEqual(s.posts()[0]!.body);                                                               // the identical request
    expect(screen.queryByRole("dialog")).toBeNull();
    expect(screen.queryByTestId("notice-expired")).toBeNull();                                                            // the session was never ended
    expect(screen.getByTestId("company-name")).toBeInTheDocument();
  });

  it("Cancel leaves the screen and its input intact and says the action was NOT done", async () => {
    const s = setup({ create: [STALE], stepUp: [] });
    await openAndFill();
    const d = await dialog();
    await userEvent.click(within(d).getByRole("button", { name: A("cancel") }));
    expect(screen.queryByRole("dialog")).toBeNull();
    expect(await screen.findByText(A("mfa.step_up.cancelled"))).toBeInTheDocument();
    expect(screen.getByLabelText("الاسم بالعربية")).toHaveValue("الخصوم");                                                 // the typed input is still there
    expect(s.posts()).toHaveLength(1);                                                                                    // no repeat
    expect(s.stepUps()).toHaveLength(0);                                                                                  // no code was sent
    expect(screen.queryByTestId("notice-expired")).toBeNull();
    expect(screen.queryByTestId("saved")).toBeNull();
  });

  it("Escape cancels like the Cancel button", async () => {
    setup({ create: [STALE], stepUp: [] });
    await openAndFill();
    await dialog();
    await userEvent.keyboard("{Escape}");
    expect(await screen.findByText(A("mfa.step_up.cancelled"))).toBeInTheDocument();
  });

  it("a wrong or replayed code keeps the dialog open, clears the field, says so, and does NOT end the session; the next good code completes the action", async () => {
    const s = setup({ create: [STALE, CREATED], stepUp: [() => json({ error: "invalid_code" }, 401), () => json({ verifiedAtUtc: "2026-11-21T10:00:00Z" })] });
    await openAndFill();
    const d = await dialog();
    const field = within(d).getByLabelText(new RegExp(A("mfa.code")));
    await userEvent.type(field, "000000");
    await userEvent.click(within(d).getByRole("button", { name: A("mfa.verify") }));
    expect(await within(d).findByTestId("step-up-error")).toHaveTextContent(A("mfa.step_up.wrong"));
    expect(field).toHaveValue("");                                                                                         // a used code is never kept
    expect(screen.queryByTestId("notice-expired")).toBeNull();
    expect(s.posts()).toHaveLength(1);                                                                                     // not repeated yet
    await userEvent.type(field, "654321");
    await userEvent.click(within(d).getByRole("button", { name: A("mfa.verify") }));
    expect(await screen.findByTestId("saved")).toBeInTheDocument();
    expect(s.stepUps().map((c) => (c.body as { code: string }).code)).toEqual(["000000", "654321"]);
    expect(s.posts()).toHaveLength(2);
  });

  it("the throttle (429) is shown as its own message and the dialog stays open", async () => {
    setup({ create: [STALE], stepUp: [() => json({ error: "too_many_attempts" }, 429, { "Retry-After": "600" })] });
    await openAndFill();
    const d = await dialog();
    await userEvent.type(within(d).getByLabelText(new RegExp(A("mfa.code"))), "111111");
    await userEvent.click(within(d).getByRole("button", { name: A("mfa.verify") }));
    expect(await within(d).findByTestId("step-up-error")).toHaveTextContent(A("mfa.throttled"));
    expect(screen.getByRole("dialog")).toBeInTheDocument();
  });

  // Since D-236 a session that ends while the form holds unsaved input asks the user to sign in again (sessionLock.test.tsx); what this test guards is unchanged:
  // a 401 that is NOT `mfa_step_up_required` is a session end, never a step-up.
  it("another 401 (the session itself ended) still ends the session and opens no step-up dialog", async () => {
    const s = setup({ create: [() => json({ error: "unauthenticated" }, 401)], stepUp: [] });
    await openAndFill();
    expect(await screen.findByTestId("reauth-dialog")).toBeInTheDocument();
    expect(screen.getAllByRole("dialog")).toHaveLength(1);                     // the sign-in-again dialog only
    expect(screen.queryByText(A("mfa.step_up.title"))).toBeNull();
    expect(screen.getByTestId("shell-root")).toHaveAttribute("hidden");
    expect(s.stepUps()).toHaveLength(0);
    expect(s.posts()).toHaveLength(1);                                         // not repeated
  });

  it("after the session ended and the same user signed in again (D-236), a privileged save asks for a fresh proof and then goes through with the restored input", async () => {
    const s = setup({ create: [() => json({ error: "unauthenticated" }, 401), STALE, CREATED], stepUp: [() => json({ verifiedAtUtc: "2026-11-21T10:00:00Z" })] });
    await openAndFill();                                                       // the save meets the ended session
    const reauth = within(await screen.findByTestId("reauth-dialog"));
    await userEvent.type(reauth.getByLabelText(new RegExp(A("login.user"))), "amal");
    await userEvent.type(reauth.getByLabelText(new RegExp(A("login.password"))), "secret-password");
    await userEvent.click(reauth.getByRole("button", { name: A("login.submit") }));
    await waitFor(() => expect(screen.queryByTestId("reauth-dialog")).toBeNull());
    expect(screen.getByLabelText("الاسم بالعربية")).toHaveValue("الخصوم");         // restored from memory
    expect(s.posts()).toHaveLength(1);                                         // nothing was sent again by itself
    await userEvent.click(screen.getByRole("button", { name: "حفظ الحساب" }));   // the user's own save: the new session has no fresh proof
    const d = await dialog();
    expect(within(d).getByText(A("mfa.step_up.title"))).toBeInTheDocument();
    await userEvent.type(within(d).getByLabelText(new RegExp(A("mfa.code"))), "123456");
    await userEvent.click(within(d).getByRole("button", { name: A("mfa.verify") }));
    expect(await screen.findByTestId("saved")).toHaveTextContent(A("accounts.created"));
    expect(s.stepUps()).toHaveLength(1);
    expect(s.posts()).toHaveLength(3);                                         // refused (session), refused (stale proof), repeated once
    expect(s.posts()[2]!.body).toEqual(s.posts()[0]!.body);                     // the same input the user typed before the session ended
  });

  it("a 401 from the step-up call itself (the session is gone) ends the session and closes the dialog", async () => {
    setup({ create: [STALE], stepUp: [() => json({ error: "unauthenticated" }, 401)] });
    await openAndFill();
    const d = await dialog();
    await userEvent.type(within(d).getByLabelText(new RegExp(A("mfa.code"))), "123456");
    await userEvent.click(within(d).getByRole("button", { name: A("mfa.verify") }));
    expect(await screen.findByTestId("reauth-dialog")).toBeInTheDocument();    // D-236: unsaved input, so sign in again; the session did end
    expect(screen.getAllByRole("dialog")).toHaveLength(1);                     // the step-up dialog is closed
    expect(screen.queryByText(A("mfa.step_up.title"))).toBeNull();
    expect(document.querySelector('input[autocomplete="one-time-code"]')).toBeNull();           // and its code is gone
  });

  it("a server that still asks after a good step-up is not looped: one repeat only, and the screen says the action was not done", async () => {
    const s = setup({ create: [STALE, STALE], stepUp: [() => json({ verifiedAtUtc: "2026-11-21T10:00:00Z" })] });
    await openAndFill();
    const d = await dialog();
    await userEvent.type(within(d).getByLabelText(new RegExp(A("mfa.code"))), "123456");
    await userEvent.click(within(d).getByRole("button", { name: A("mfa.verify") }));
    expect(await screen.findByText(A("mfa.step_up.required"))).toBeInTheDocument();
    expect(s.posts()).toHaveLength(2);
    expect(s.stepUps()).toHaveLength(1);
    expect(screen.queryByTestId("notice-expired")).toBeNull();
  });

  it("Enter submits the code, a code of the wrong shape is refused before any request, and Arabic-Indic digits are sent as Latin digits", async () => {
    const s = setup({ create: [STALE, CREATED], stepUp: [() => json({ verifiedAtUtc: "2026-11-21T10:00:00Z" })] });
    await openAndFill();
    const d = await dialog();
    const field = within(d).getByLabelText(new RegExp(A("mfa.code")));
    await userEvent.type(field, "12345{Enter}");
    expect(await within(d).findByText(A("mfa.step_up.format"))).toBeInTheDocument();
    expect(s.stepUps()).toHaveLength(0);
    await userEvent.clear(field);
    await userEvent.type(field, "١٢٣٤٥٦{Enter}");
    expect(await screen.findByTestId("saved")).toBeInTheDocument();
    expect(s.stepUps()[0]!.body).toEqual({ code: "123456" });
  });

  it("the code is never written to browser storage and is gone from the page after the dialog closes", async () => {
    setup({ create: [STALE, CREATED], stepUp: [() => json({ verifiedAtUtc: "2026-11-21T10:00:00Z" })] });
    await openAndFill();
    const d = await dialog();
    await userEvent.type(within(d).getByLabelText(new RegExp(A("mfa.code"))), "424242");
    expect(JSON.stringify({ ...localStorage, ...sessionStorage })).not.toContain("424242");
    await userEvent.click(within(d).getByRole("button", { name: A("mfa.verify") }));
    await screen.findByTestId("saved");
    expect(JSON.stringify({ ...localStorage, ...sessionStorage })).not.toContain("424242");
    expect(document.body.innerHTML).not.toContain("424242");
    expect(document.cookie).not.toContain("424242");
  });

  it("in English the dialog is left to right and worded in English", async () => {
    localStorage.setItem("hasm.locale", "en");
    setup({ create: [STALE], stepUp: [] });
    await screen.findByTestId("company-name");
    window.location.hash = "#/company/accounts";
    fireEvent(window, new HashChangeEvent("hashchange"));
    await userEvent.click(await screen.findByRole("button", { name: E("accounts.new") }));
    await userEvent.selectOptions(screen.getByLabelText(new RegExp(`^${E("accounts.type")}`)), "liability");
    await userEvent.type(screen.getByLabelText(E("accounts.name_en")), "Payables");
    await userEvent.click(screen.getByRole("button", { name: E("accounts.save") }));
    const d = await dialog();
    expect(within(d).getByText(E("mfa.step_up.title"))).toBeInTheDocument();
    expect(within(d).getByText(E("mfa.step_up.message"))).toBeInTheDocument();
    expect(document.documentElement.dir).toBe("ltr");
    await userEvent.click(within(d).getByRole("button", { name: E("cancel") }));
    expect(await screen.findByText(E("mfa.step_up.cancelled"))).toBeInTheDocument();
  });

  it("several refusals at once share ONE dialog and are all repeated after one good code", async () => {
    const s = setup({ create: [], stepUp: [() => json({ verifiedAtUtc: "2026-11-21T10:00:00Z" })] });
    await screen.findByTestId("company-name");
    const results: Promise<boolean>[] = [s.step.request(), s.step.request(), s.step.request()];
    const d = await dialog();
    expect(screen.getAllByRole("dialog")).toHaveLength(1);
    await userEvent.type(within(d).getByLabelText(new RegExp(A("mfa.code"))), "123456{Enter}");
    expect(await Promise.all(results)).toEqual([true, true, true]);
    expect(s.stepUps()).toHaveLength(1);
  });

  it("the message catalogs carry the same step-up keys in Arabic and English", () => {
    const keys = Object.keys(catalog.ar).filter((k) => k.startsWith("mfa.step_up."));
    expect(keys.length).toBe(6);                                                                                             // (error.mfa_* are checked by the error-code coverage test)
    expect(keys.sort()).toEqual(Object.keys(catalog.en).filter((k) => k.startsWith("mfa.step_up.")).sort());
  });
});
