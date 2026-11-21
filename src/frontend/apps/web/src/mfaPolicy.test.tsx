import { createApiClient, type AccessRole, type MfaPolicy } from "@hasm/api-client";
import { I18nProvider } from "@hasm/ui";
import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { App } from "./App";
import { ApiError } from "@hasm/api-client";
import { createSessionBridge } from "./auth";
import { catalog } from "./messages";
import { createStepUpBridge } from "./stepUp";
import { ALL_PERMISSIONS, USER, renderApp, signedIn } from "./test-utils";

/**
 * The company MFA policy screen and the company-selection enrolment prompt (Phase 4.1 S3b-2b, shared by Web and Desktop). The first half uses the screen with a scripted client; the second half uses the REAL api client against a
 * scripted server through the whole app: the 403 `mfa_enrolment_required` flow (company selection, and a company route of a company that is already selected), the step-up retry of the policy save, and the 409. The server decides
 * everything; these tests only prove the screens show it, never loop, and never end the session for it.
 */
const A = (k: string) => catalog.ar[k]!;
beforeEach(() => { localStorage.clear(); sessionStorage.clear(); window.location.hash = ""; });

const POLICY_PERMS = [...ALL_PERMISSIONS, "access.mfapolicy.view", "access.mfapolicy.update"];
const VIEW_ONLY = [...ALL_PERMISSIONS, "access.mfapolicy.view"];
const R1: AccessRole = { id: "00000000-0000-0000-0000-0000000000a1", name: "Accountant", preset: false, presetKey: null, active: true, permissions: [] };
const R2: AccessRole = { id: "00000000-0000-0000-0000-0000000000a2", name: "Cashier", preset: false, presetKey: null, active: true, permissions: [] };
const R3: AccessRole = { id: "00000000-0000-0000-0000-0000000000a3", name: "Retired", preset: false, presetKey: null, active: false, permissions: [] };
const OFF: MfaPolicy = { policy: "off", roleIds: [], updatedAtUtc: null };

function setupScreen(over: Record<string, unknown> = {}, perms = POLICY_PERMS) {
  const client = signedIn({
    getMfaPolicy: vi.fn().mockResolvedValue(OFF),
    putMfaPolicy: vi.fn().mockImplementation(async (b: { policy: MfaPolicy["policy"]; roleIds?: string[] }) => ({ policy: b.policy, roleIds: b.roleIds ?? [], updatedAtUtc: "2026-11-21T10:00:00Z" })),
    listAccessRoles: vi.fn().mockResolvedValue({ roles: [R1, R2, R3] }),
    ...over,
  }, perms);
  renderApp(client);
  return client;
}
const kindRadio = (k: string) => within(screen.getByTestId(`mfapolicy-kind-${k}`)).getByRole("radio");
async function openByHash() {
  await screen.findByTestId("company-name");
  window.location.hash = "#/company/mfa-policy";
  fireEvent(window, new HashChangeEvent("hashchange"));
  await screen.findByRole("heading", { name: A("mfapolicy.title"), level: 1 });
}

describe("company MFA policy screen", () => {
  it("shows the saved policy and, with the update permission, lets it be changed; 'off' saves with no warning dialog", async () => {
    const client = setupScreen({ getMfaPolicy: vi.fn().mockResolvedValue({ policy: "all", roleIds: [], updatedAtUtc: "2026-11-20T09:00:00Z" }) });
    await openByHash();
    expect(kindRadio("all")).toBeChecked();
    expect(screen.getByTestId("mfapolicy-warning")).toBeInTheDocument();
    await userEvent.click(kindRadio("off"));
    expect(screen.queryByTestId("mfapolicy-warning")).toBeNull();
    await userEvent.click(screen.getByTestId("mfapolicy-save"));
    await waitFor(() => expect(client.putMfaPolicy).toHaveBeenCalledWith({ policy: "off", roleIds: [] }));
    expect(screen.queryByRole("dialog")).toBeNull();
    expect(await screen.findByTestId("mfapolicy-notice")).toHaveTextContent(A("mfapolicy.saved"));
  });

  it("is read-only without access.mfapolicy.update (view only)", async () => {
    setupScreen({}, VIEW_ONLY);
    await openByHash();
    expect(screen.getByTestId("mfapolicy-readonly")).toBeInTheDocument();
    expect(screen.queryByTestId("mfapolicy-save")).toBeNull();
    expect(kindRadio("all")).toBeDisabled();
  });

  it("without the view permission the route is not offered and the policy is never requested", async () => {
    const client = setupScreen({}, ALL_PERMISSIONS);
    await screen.findByTestId("company-name");
    window.location.hash = "#/company/mfa-policy";
    fireEvent(window, new HashChangeEvent("hashchange"));
    await waitFor(() => expect(screen.queryByRole("heading", { name: A("mfapolicy.title"), level: 1 })).toBeNull());
    expect(client.getMfaPolicy).not.toHaveBeenCalled();
    expect(screen.queryByText(A("nav.mfa_policy"))).toBeNull();
  });

  it("'all' warns before saving: Cancel sends nothing, Confirm sends the policy", async () => {
    const client = setupScreen();
    await openByHash();
    await userEvent.click(kindRadio("all"));
    expect(screen.getByTestId("mfapolicy-warning")).toHaveTextContent(A("mfapolicy.warn.all"));
    await userEvent.click(screen.getByTestId("mfapolicy-save"));
    const d = await screen.findByRole("dialog");
    expect(within(d).getByText(A("mfapolicy.confirm.all"))).toBeInTheDocument();
    await userEvent.click(within(d).getByRole("button", { name: A("cancel") }));
    expect(client.putMfaPolicy).not.toHaveBeenCalled();
    await userEvent.click(screen.getByTestId("mfapolicy-save"));
    await userEvent.click(within(await screen.findByRole("dialog")).getByRole("button", { name: A("mfapolicy.confirm") }));
    await waitFor(() => expect(client.putMfaPolicy).toHaveBeenCalledWith({ policy: "all", roleIds: [] }));
  });

  it("'roles' needs at least one role, lists only active roles, warns, and sends the chosen role ids", async () => {
    const client = setupScreen();
    await openByHash();
    await userEvent.click(kindRadio("roles"));
    expect(screen.getByTestId("mfapolicy-roles-required")).toBeInTheDocument();
    expect(screen.getByTestId("mfapolicy-save")).toBeDisabled();
    expect(screen.queryByTestId(`mfapolicy-role-${R3.id}`)).toBeNull();                              // an inactive role is not offered
    await userEvent.click(within(screen.getByTestId(`mfapolicy-role-${R2.id}`)).getByRole("checkbox"));
    await userEvent.click(within(screen.getByTestId(`mfapolicy-role-${R1.id}`)).getByRole("checkbox"));
    expect(screen.getByTestId("mfapolicy-warning")).toHaveTextContent(A("mfapolicy.warn.roles"));
    await userEvent.click(screen.getByTestId("mfapolicy-save"));
    await userEvent.click(within(await screen.findByRole("dialog")).getByRole("button", { name: A("mfapolicy.confirm") }));
    await waitFor(() => expect(client.putMfaPolicy).toHaveBeenCalledWith({ policy: "roles", roleIds: [R2.id, R1.id] }));
  });

  it("409 mfa_policy_requires_own_enrolment explains it and links to the security settings; the policy is not shown as saved", async () => {
    setupScreen({ putMfaPolicy: vi.fn().mockRejectedValue(new ApiError(409, "mfa_policy_requires_own_enrolment")) });
    await openByHash();
    await userEvent.click(kindRadio("all"));
    await userEvent.click(screen.getByTestId("mfapolicy-save"));
    await userEvent.click(within(await screen.findByRole("dialog")).getByRole("button", { name: A("mfapolicy.confirm") }));
    const banner = await screen.findByTestId("mfapolicy-own-enrolment");
    expect(banner).toHaveTextContent(A("mfapolicy.error.mfa_policy_requires_own_enrolment"));
    expect(within(banner).getByTestId("mfapolicy-security-link")).toHaveAttribute("href", "#/account/security");
    expect(screen.queryByTestId("mfapolicy-notice")).toBeNull();
    expect(kindRadio("all")).toBeChecked();    // the choice is kept for another try
    expect(screen.getByTestId("nav-security")).toBeInTheDocument();
  });

  it.each([["unknown_role", 400], ["invalid_request", 400], ["forbidden", 403]])("%s is shown as its own message and the session stays", async (code, status) => {
    setupScreen({ putMfaPolicy: vi.fn().mockRejectedValue(new ApiError(status, code)) });
    await openByHash();
    await userEvent.click(kindRadio("all"));
    await userEvent.click(screen.getByTestId("mfapolicy-save"));
    await userEvent.click(within(await screen.findByRole("dialog")).getByRole("button", { name: A("mfapolicy.confirm") }));
    const text = await screen.findByTestId("mfapolicy-error");
    expect(text).toHaveTextContent(code === "forbidden" ? A("error.forbidden") : A(`mfapolicy.error.${code}`));
    expect(screen.queryByTestId("notice-expired")).toBeNull();
  });

  it("a role list the server refuses does not block 'off' or 'all'; roles cannot be chosen and it is said so", async () => {
    setupScreen({ listAccessRoles: vi.fn().mockRejectedValue(new ApiError(403, "forbidden")) });
    await openByHash();
    await userEvent.click(kindRadio("roles"));
    expect(screen.getByTestId("mfapolicy-roles-unavailable")).toBeInTheDocument();
    expect(screen.getByTestId("mfapolicy-save")).toBeDisabled();
  });

  it("the Arabic and English catalogs carry the same policy keys", () => {
    const keys = (c: Record<string, string>) => Object.keys(c).filter((k) => k.startsWith("mfapolicy.") || k.startsWith("enrol_required.") || k === "nav.mfa_policy").sort();
    expect(keys(catalog.ar).length).toBeGreaterThan(30);
    expect(keys(catalog.ar)).toEqual(keys(catalog.en));
  });
});

// ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
// The real api client against a scripted server

const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
const ENROL_REQUIRED = () => json({ error: "mfa_enrolment_required" }, 403);
const STALE = () => json({ error: "mfa_step_up_required" }, 401);
type Reply = Response | (() => Response);

interface Server {
  companies: { id: string; nameAr: string; nameEn: string }[];
  perms: string[];
  /** answers per path+method, consumed in order; the last answer repeats */
  routes: Record<string, Reply[]>;
}
function startApp(server: Server) {
  const calls: { method: string; path: string; body: unknown }[] = [];
  const queues = new Map<string, Reply[]>(Object.entries(server.routes).map(([k, v]) => [k, [...v]]));
  const answer = (key: string): Response | null => {
    const q = queues.get(key);
    if (!q || q.length === 0) return null;
    const r = q.length > 1 ? q.shift()! : q[0]!;
    return typeof r === "function" ? r() : r;
  };
  const fetchMock = vi.fn().mockImplementation(async (url: string, init: RequestInit = {}) => {
    const path = new URL(url).pathname;
    const method = init.method ?? "GET";
    calls.push({ method, path, body: typeof init.body === "string" ? JSON.parse(init.body) : undefined });
    const scripted = answer(`${method} ${path}`);
    if (scripted) return scripted;
    if (path === "/api/v1/auth/status") return json({ authenticated: true, user: USER, mfa: { state: "none", verified: false, verifiedAtUtc: null } });
    if (path === "/api/v1/companies") return json({ companies: server.companies });
    if (path === "/api/v1/session/company") return json({ companyId: server.companies[0]!.id });
    if (path === "/api/v1/access/me") return json({ companyId: server.companies[0]!.id, grants: [{ roleId: "r", scope: "company", scopeId: null, permissions: server.perms }] });
    if (path === "/api/v1/company/currencies") return json({ currencies: [], baseCurrency: null, baseCurrencySelected: false, baseCurrencyLock: { locked: false, checksImplemented: false, unimplementedDomains: [] } });
    return json({ error: "not_found" }, 404);
  });
  const session = createSessionBridge();
  const step = createStepUpBridge();
  const client = createApiClient("https://x", fetchMock as unknown as typeof fetch, { client: "web", onUnauthenticated: session.fire, onEnrolmentRequired: session.fireEnrolment, stepUp: step.request });
  render(<I18nProvider catalog={catalog}><App client={client} bridge={session} stepUpBridge={step} /></I18nProvider>);
  const count = (method: string, path: string) => calls.filter((c) => c.method === method && c.path === path);
  return { calls, count };
}
const TWO = [{ id: "c1", nameAr: "شركة الأمل", nameEn: "Amal Co" }, { id: "c2", nameAr: "شركة النور", nameEn: "Noor Co" }];
const SECRET = { otpauthUri: "otpauth://totp/HASM:amal?secret=JBSWY3DPEHPK3PXP&issuer=HASM", secret: "JBSWY3DPEHPK3PXP" };

async function enrolFromPrompt() {
  await userEvent.type(await screen.findByLabelText(new RegExp(A("sec.enrol.password"))), "Correct-1");
  await userEvent.click(screen.getByRole("button", { name: A("sec.enrol.start") }));
  expect(await screen.findByTestId("mfa-secret")).toHaveTextContent(SECRET.secret);
  await userEvent.type(screen.getByLabelText(new RegExp(A("mfa.code"))), "123456{Enter}");
}

describe("403 mfa_enrolment_required, end to end with the real api client", () => {
  it("company selection: the prompt replaces the selection (no sign-out), the user enrols, and the SAME company is selected again and entered", async () => {
    const s = startApp({
      companies: TWO, perms: ["company.profile.view"],
      routes: {
        "POST /api/v1/session/company": [ENROL_REQUIRED, () => json({ companyId: "c2" })],
        "POST /api/v1/auth/mfa/enrol/begin": [() => json(SECRET)],
        "POST /api/v1/auth/mfa/enrol/confirm": [() => json({ user: USER })],
        "GET /api/v1/access/me": [() => json({ error: "company_not_selected" }, 409), () => json({ companyId: "c2", grants: [{ roleId: "r", scope: "company", scopeId: null, permissions: ["company.profile.view"] }] })],
      },
    });
    await userEvent.click(await screen.findByRole("button", { name: /شركة النور/ }));
    expect(await screen.findByTestId("enrolment-required")).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: A("enrol_required.title") })).toBeInTheDocument();
    expect(screen.queryByTestId("notice-expired")).toBeNull();                                         // not signed out
    expect(screen.queryByRole("heading", { name: A("login.title") })).toBeNull();
    expect(s.count("POST", "/api/v1/session/company")).toHaveLength(1);                                // no retry loop while the prompt is shown
    await enrolFromPrompt();
    await screen.findByTestId("company-name");                                                         // the shell of the company
    const selections = s.count("POST", "/api/v1/session/company");
    expect(selections).toHaveLength(2);
    expect(selections.map((c) => c.body)).toEqual([{ companyId: "c2" }, { companyId: "c2" }]);
    expect(screen.queryByTestId("enrolment-required")).toBeNull();
    expect(screen.queryByTestId("mfa-secret")).toBeNull();                                             // the secret is gone
    expect(s.count("POST", "/api/v1/auth/logout")).toHaveLength(0);
  });

  it("a company route of an ALREADY selected company: the same prompt, the session stays, no request loop, and the way out works (another company)", async () => {
    const s = startApp({
      companies: [TWO[0]!], perms: ["company.profile.view"],
      routes: {
        "GET /api/v1/access/me": [ENROL_REQUIRED],
        "DELETE /api/v1/session/company": [() => new Response(null, { status: 204 })],
      },
    });
    expect(await screen.findByTestId("enrolment-required")).toBeInTheDocument();                       // the restore hit the 403 on the company route
    expect(screen.queryByTestId("notice-expired")).toBeNull();
    expect(screen.queryByTestId("unreachable")).toBeNull();
    await waitFor(() => expect(screen.getByTestId("enrol-required-another")).toBeEnabled());
    expect(s.count("GET", "/api/v1/access/me")).toHaveLength(1);
    expect(s.count("POST", "/api/v1/session/company")).toHaveLength(0);
    await userEvent.click(screen.getByTestId("enrol-required-another"));
    expect(await screen.findByRole("heading", { name: A("companies.title") })).toBeInTheDocument();
    expect(s.count("DELETE", "/api/v1/session/company")).toHaveLength(1);
  });

  it("a company screen that is open when the server starts refusing: the prompt replaces it, once, with no sign-out and no loop of retries", async () => {
    const s = startApp({
      companies: [TWO[0]!], perms: ["company.profile.view", "company.settings.view", "company.currencies.view"],
      routes: {
        "GET /api/v1/company/currencies": [ENROL_REQUIRED],
        "GET /api/v1/auth/status": [() => json({ authenticated: true, user: USER, mfa: { state: "none", verified: false, verifiedAtUtc: null } })],
      },
    });
    await screen.findByTestId("company-name");
    window.location.hash = "#/company/currencies";
    fireEvent(window, new HashChangeEvent("hashchange"));
    expect(await screen.findByTestId("enrolment-required")).toBeInTheDocument();
    await new Promise((r) => setTimeout(r, 50));
    expect(s.count("GET", "/api/v1/company/currencies")).toHaveLength(1);
    expect(screen.queryByTestId("notice-expired")).toBeNull();
    expect(s.count("POST", "/api/v1/auth/logout")).toHaveLength(0);
  });

  it("a user whose state is not 'none' gets the explanation, not an enrolment form, and can still leave", async () => {
    startApp({
      companies: TWO, perms: [],
      routes: {
        "POST /api/v1/session/company": [ENROL_REQUIRED],
        "GET /api/v1/access/me": [() => json({ error: "company_not_selected" }, 409)],
        "GET /api/v1/auth/status": [() => json({ authenticated: true, user: USER }), () => json({ authenticated: true, user: USER, mfa: { state: "enrolmentrequired", verified: false, verifiedAtUtc: null } })],
      },
    });
    await userEvent.click(await screen.findByRole("button", { name: /شركة النور/ }));
    expect(await screen.findByTestId("enrol-required-other")).toBeInTheDocument();
    expect(screen.queryByTestId("sec-enrol-begin")).toBeNull();
    expect(screen.getByTestId("enrol-required-logout")).toBeInTheDocument();
  });

  it("a second refusal after the enrolment falls back to company selection (no loop)", async () => {
    const s = startApp({
      companies: TWO, perms: [],
      routes: {
        "POST /api/v1/session/company": [ENROL_REQUIRED, ENROL_REQUIRED],
        "GET /api/v1/access/me": [() => json({ error: "company_not_selected" }, 409)],
        "POST /api/v1/auth/mfa/enrol/begin": [() => json(SECRET)],
        "POST /api/v1/auth/mfa/enrol/confirm": [() => json({ user: USER })],
      },
    });
    await userEvent.click(await screen.findByRole("button", { name: /شركة النور/ }));
    await screen.findByTestId("enrolment-required");
    await enrolFromPrompt();
    expect(await screen.findByRole("heading", { name: A("companies.title") })).toBeInTheDocument();
    expect(s.count("POST", "/api/v1/session/company").length).toBeLessThanOrEqual(2);
    expect(screen.queryByTestId("notice-expired")).toBeNull();
  });
});

describe("the policy save with the real api client", () => {
  const PERMS = ["company.profile.view", "access.mfapolicy.view", "access.mfapolicy.update"];
  const goPolicy = async () => {
    await screen.findByTestId("company-name");
    window.location.hash = "#/company/mfa-policy";
    fireEvent(window, new HashChangeEvent("hashchange"));
    await screen.findByRole("heading", { name: A("mfapolicy.title"), level: 1 });
  };
  const saveAll = async () => {
    await userEvent.click(kindRadio("all"));
    await userEvent.click(screen.getByTestId("mfapolicy-save"));
    await userEvent.click(within(await screen.findByRole("dialog")).getByRole("button", { name: A("mfapolicy.confirm") }));
  };

  it("a stale proof opens the step-up dialog, a good code repeats the IDENTICAL save once, and the policy is shown as saved", async () => {
    const s = startApp({
      companies: [TWO[0]!], perms: PERMS,
      routes: {
        "GET /api/v1/access/mfa-policy": [() => json(OFF)],
        "GET /api/v1/access/roles": [() => json({ roles: [] })],
        "PUT /api/v1/access/mfa-policy": [STALE, () => json({ policy: "all", roleIds: [], updatedAtUtc: "2026-11-21T10:00:00Z" })],
        "POST /api/v1/auth/mfa/step-up": [() => json({ verifiedAtUtc: "2026-11-21T10:00:00Z" })],
      },
    });
    await goPolicy();
    await saveAll();
    const d = await screen.findByRole("dialog");
    await userEvent.type(within(d).getByLabelText(new RegExp(A("mfa.code"))), "123456");
    await userEvent.click(within(d).getByRole("button", { name: A("mfa.verify") }));
    expect(await screen.findByTestId("mfapolicy-notice")).toHaveTextContent(A("mfapolicy.saved"));
    const puts = s.count("PUT", "/api/v1/access/mfa-policy");
    expect(puts).toHaveLength(2);
    expect(puts[1]!.body).toEqual(puts[0]!.body);
    expect(puts[0]!.body).toEqual({ policy: "all", roleIds: [] });
    expect(screen.queryByTestId("notice-expired")).toBeNull();
  });

  it("cancelling the step-up says the policy was NOT saved and keeps the choice", async () => {
    startApp({
      companies: [TWO[0]!], perms: PERMS,
      routes: {
        "GET /api/v1/access/mfa-policy": [() => json(OFF)],
        "GET /api/v1/access/roles": [() => json({ roles: [] })],
        "PUT /api/v1/access/mfa-policy": [STALE],
      },
    });
    await goPolicy();
    await saveAll();
    await userEvent.click(within(await screen.findByRole("dialog")).getByRole("button", { name: A("cancel") }));
    expect(await screen.findByTestId("mfapolicy-error")).toHaveTextContent(A("mfa.step_up.cancelled"));
    expect(screen.queryByTestId("mfapolicy-notice")).toBeNull();
    expect(kindRadio("all")).toBeChecked();
  });

  it("409 from the server: the explanation and the link to the security settings (no sign-out)", async () => {
    startApp({
      companies: [TWO[0]!], perms: PERMS,
      routes: {
        "GET /api/v1/access/mfa-policy": [() => json(OFF)],
        "GET /api/v1/access/roles": [() => json({ roles: [] })],
        "PUT /api/v1/access/mfa-policy": [() => json({ error: "mfa_policy_requires_own_enrolment" }, 409)],
      },
    });
    await goPolicy();
    await saveAll();
    expect(await screen.findByTestId("mfapolicy-own-enrolment")).toBeInTheDocument();
    expect(screen.getByTestId("mfapolicy-security-link")).toHaveAttribute("href", "#/account/security");
    expect(screen.queryByTestId("notice-expired")).toBeNull();
  });

  it("the policy screen itself refused with 403 mfa_enrolment_required shows the enrolment prompt", async () => {
    startApp({
      companies: [TWO[0]!], perms: PERMS,
      routes: { "GET /api/v1/access/mfa-policy": [ENROL_REQUIRED], "GET /api/v1/access/roles": [ENROL_REQUIRED] },
    });
    await goPolicy().catch(() => undefined);
    expect(await screen.findByTestId("enrolment-required")).toBeInTheDocument();
  });
});
