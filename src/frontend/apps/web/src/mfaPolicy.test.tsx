import { type AccessRole, type MfaPolicy } from "@hasm/api-client";
import { I18nProvider } from "@hasm/ui";
import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { App } from "./App";
import { ApiError } from "@hasm/api-client";
import { catalog } from "./messages";
import { ALL_PERMISSIONS, USER, renderApp, signedIn } from "./test-utils";

/**
 * The company MFA policy screen and the company-selection enrolment prompt (Phase 4.1 S3b-2b, shared by Web and Desktop). The first half uses the screen with a scripted client; the second half uses the REAL api client against a
 * scripted server through the whole app: the 403 `mfa_enrolment_required` flow (company selection, and a company route of a company that is already selected), the step-up retry of the policy save, and the 409. The server decides
 * everything; these tests only prove the screens show it, never loop, and never end the session for it.
 */
const A = (k: string) => catalog.ar[k]!;
const E = (k: string) => catalog.en[k]!;
beforeEach(() => { localStorage.clear(); sessionStorage.clear(); window.location.hash = ""; });
afterEach(() => { vi.unstubAllGlobals(); });

const POLICY_PERMS = [...ALL_PERMISSIONS, "access.mfapolicy.view", "access.mfapolicy.update"];
const VIEW_ONLY = [...ALL_PERMISSIONS, "access.mfapolicy.view"];
const R1: AccessRole = { id: "00000000-0000-0000-0000-0000000000a1", name: "Accountant", preset: false, presetKey: null, active: true, permissions: [] };
const R2: AccessRole = { id: "00000000-0000-0000-0000-0000000000a2", name: "Cashier", preset: false, presetKey: null, active: true, permissions: [] };
const R3: AccessRole = { id: "00000000-0000-0000-0000-0000000000a3", name: "Retired", preset: false, presetKey: null, active: false, permissions: [] };
const OFF: MfaPolicy = { policy: "off", roleIds: [], updatedAtUtc: null, revision: 3 };

function setupScreen(over: Record<string, unknown> = {}, perms = POLICY_PERMS) {
  const client = signedIn({
    getMfaPolicy: vi.fn().mockResolvedValue(OFF),
    putMfaPolicy: vi.fn().mockImplementation(async (b: { policy: MfaPolicy["policy"]; roleIds?: string[] }, revision: number) => ({ policy: b.policy, roleIds: b.roleIds ?? [], updatedAtUtc: "2026-11-21T10:00:00Z", revision: revision + 1 })),
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
    const client = setupScreen({ getMfaPolicy: vi.fn().mockResolvedValue({ policy: "all", roleIds: [], updatedAtUtc: "2026-11-20T09:00:00Z", revision: 8 }) });
    await openByHash();
    expect(kindRadio("all")).toBeChecked();
    expect(screen.getByTestId("mfapolicy-warning")).toBeInTheDocument();
    await userEvent.click(kindRadio("off"));
    expect(screen.queryByTestId("mfapolicy-warning")).toBeNull();
    await userEvent.click(screen.getByTestId("mfapolicy-save"));
    await waitFor(() => expect(client.putMfaPolicy).toHaveBeenCalledWith({ policy: "off", roleIds: [] }, 8));
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
    await waitFor(() => expect(client.putMfaPolicy).toHaveBeenCalledWith({ policy: "all", roleIds: [] }, 3));
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
    await waitFor(() => expect(client.putMfaPolicy).toHaveBeenCalledWith({ policy: "roles", roleIds: [R2.id, R1.id] }, 3));
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

  const chooseRoles = async (...ids: string[]) => {
    await userEvent.click(kindRadio("roles"));
    for (const id of ids) await userEvent.click(within(screen.getByTestId(`mfapolicy-role-${id}`)).getByRole("checkbox"));
  };
  const confirmSave = async () => {
    await userEvent.click(screen.getByTestId("mfapolicy-save"));
    await userEvent.click(within(await screen.findByRole("dialog")).getByRole("button", { name: /^(حفظ السياسة|Save policy)$/ }));
  };

  it("M1: a language switch with an unsaved edit keeps the edit and does NOT ask the server again", async () => {
    const client = setupScreen();
    await openByHash();
    await chooseRoles(R2.id);
    expect(client.getMfaPolicy).toHaveBeenCalledTimes(1);
    fireEvent.click(screen.getByRole("button", { name: "English" }));
    expect(await screen.findByRole("heading", { name: E("mfapolicy.title"), level: 1 })).toBeInTheDocument();
    await new Promise((r) => setTimeout(r, 30));                                                       // a re-fetch, if any, would have been sent and answered by now
    expect(kindRadio("roles")).toBeChecked();
    expect(within(screen.getByTestId(`mfapolicy-role-${R2.id}`)).getByRole("checkbox")).toBeChecked();
    expect(within(screen.getByTestId(`mfapolicy-role-${R1.id}`)).getByRole("checkbox")).not.toBeChecked();
    expect(screen.getByTestId("mfapolicy-save")).toBeEnabled();                                        // still an unsaved change
    expect(client.getMfaPolicy).toHaveBeenCalledTimes(1);
    expect(client.listAccessRoles).toHaveBeenCalledTimes(1);
    fireEvent.click(screen.getByRole("button", { name: "العربية" }));
    await screen.findByRole("heading", { name: A("mfapolicy.title"), level: 1 });
    expect(kindRadio("roles")).toBeChecked();
    expect(client.getMfaPolicy).toHaveBeenCalledTimes(1);
  });

  it("M1: a failure message follows the language without a new request", async () => {
    const client = setupScreen({ putMfaPolicy: vi.fn().mockRejectedValue(new ApiError(400, "invalid_request")) });
    await openByHash();
    await userEvent.click(kindRadio("all"));
    await confirmSave();
    expect(await screen.findByTestId("mfapolicy-error")).toHaveTextContent(A("mfapolicy.error.invalid_request"));
    fireEvent.click(screen.getByRole("button", { name: "English" }));
    await waitFor(() => expect(screen.getByTestId("mfapolicy-error")).toHaveTextContent(E("mfapolicy.error.invalid_request")));
    expect(kindRadio("all")).toBeChecked();
    expect(client.getMfaPolicy).toHaveBeenCalledTimes(1);
  });

  it("M2: 400 unknown_role refreshes ONLY the role list and keeps the edit; a role that no longer exists is removed from the choice and that is said; an inactive one stays, marked", async () => {
    const R1_INACTIVE = { ...R1, active: false };
    const client = setupScreen({
      putMfaPolicy: vi.fn().mockRejectedValue(new ApiError(400, "unknown_role")),
      listAccessRoles: vi.fn().mockResolvedValueOnce({ roles: [R1, R2, R3] }).mockResolvedValue({ roles: [R1_INACTIVE] }),      // meanwhile R2 was deleted and R1 deactivated
    });
    await openByHash();
    await chooseRoles(R1.id, R2.id);
    await confirmSave();
    expect(await screen.findByTestId("mfapolicy-error")).toHaveTextContent(A("mfapolicy.error.unknown_role"));
    const note = await screen.findByTestId("mfapolicy-roles-refreshed");
    expect(note).toHaveTextContent(A("mfapolicy.roles_refreshed"));
    expect(note).toHaveTextContent(A("mfapolicy.roles_dropped").replace("{count}", "1"));
    expect(kindRadio("roles")).toBeChecked();                                                          // the edit is NOT reset to the saved 'off'
    expect(kindRadio("off")).not.toBeChecked();
    expect(client.getMfaPolicy).toHaveBeenCalledTimes(1);                                              // the policy itself is not fetched again
    expect(client.listAccessRoles).toHaveBeenCalledTimes(2);
    expect(screen.queryByTestId(`mfapolicy-role-${R2.id}`)).toBeNull();                                // gone from the list
    expect(screen.queryByTestId(`mfapolicy-role-unavailable-${R2.id}`)).toBeNull();                    // and from the choice
    expect(within(screen.getByTestId(`mfapolicy-role-unavailable-${R1.id}`)).getByRole("checkbox")).toBeChecked();   // kept, shown as not available: the user decides
    expect(screen.getByTestId("mfapolicy-save")).toBeEnabled();
  });

  it("M2: when every chosen role still exists nothing is removed and no removal is claimed; a failed refresh keeps everything", async () => {
    setupScreen({ putMfaPolicy: vi.fn().mockRejectedValue(new ApiError(400, "unknown_role")) });
    await openByHash();
    await chooseRoles(R1.id);
    await confirmSave();
    const note = await screen.findByTestId("mfapolicy-roles-refreshed");
    expect(note).toHaveTextContent(A("mfapolicy.roles_refreshed"));
    expect(note.textContent).toBe(A("mfapolicy.roles_refreshed"));
    expect(within(screen.getByTestId(`mfapolicy-role-${R1.id}`)).getByRole("checkbox")).toBeChecked();
  });

  it("M2: a role refresh the server refuses leaves the list and the choice as they were", async () => {
    setupScreen({
      putMfaPolicy: vi.fn().mockRejectedValue(new ApiError(400, "unknown_role")),
      listAccessRoles: vi.fn().mockResolvedValueOnce({ roles: [R1, R2, R3] }).mockRejectedValue(new ApiError(500, "server_error")),
    });
    await openByHash();
    await chooseRoles(R1.id);
    await confirmSave();
    expect(await screen.findByTestId("mfapolicy-error")).toHaveTextContent(A("mfapolicy.error.unknown_role"));
    expect(screen.queryByTestId("mfapolicy-roles-refreshed")).toBeNull();
    expect(within(screen.getByTestId(`mfapolicy-role-${R1.id}`)).getByRole("checkbox")).toBeChecked();
    expect(screen.getByTestId(`mfapolicy-role-${R2.id}`)).toBeInTheDocument();
  });

  it("L5: 412 revision_conflict keeps the edit, says someone else changed the policy and offers a reload; the reload takes the new revision WITHOUT discarding the edit, and the next save sends it", async () => {
    const putMfaPolicy = vi.fn()
      .mockRejectedValueOnce(new ApiError(412, "revision_conflict", { currentRevision: 9 }))
      .mockImplementation(async (b: { policy: MfaPolicy["policy"]; roleIds?: string[] }, revision: number) => ({ policy: b.policy, roleIds: b.roleIds ?? [], updatedAtUtc: "2026-11-21T10:00:00Z", revision: revision + 1 }));
    const getMfaPolicy = vi.fn().mockResolvedValueOnce(OFF).mockResolvedValue({ policy: "all", roleIds: [], updatedAtUtc: "2026-11-21T09:00:00Z", revision: 9 });
    const client = setupScreen({ putMfaPolicy, getMfaPolicy });
    await openByHash();
    await chooseRoles(R2.id);
    await confirmSave();
    const banner = await screen.findByTestId("mfapolicy-conflict");
    expect(banner).toHaveTextContent(A("mfapolicy.conflict"));
    expect(putMfaPolicy).toHaveBeenLastCalledWith({ policy: "roles", roleIds: [R2.id] }, 3);
    expect(screen.queryByTestId("mfapolicy-notice")).toBeNull();
    expect(kindRadio("roles")).toBeChecked();
    expect(client.getMfaPolicy).toHaveBeenCalledTimes(1);                                              // nothing is fetched until the user asks
    await userEvent.click(within(banner).getByTestId("mfapolicy-reload"));
    const reloaded = await screen.findByTestId("mfapolicy-reloaded");
    expect(reloaded).toHaveTextContent(A("mfapolicy.reloaded").replace("{policy}", A("mfapolicy.kind.all")));      // what is in force now is named
    expect(reloaded).toHaveTextContent(A("mfapolicy.reloaded.kept"));
    expect(screen.queryByTestId("mfapolicy-conflict")).toBeNull();
    expect(kindRadio("roles")).toBeChecked();                                                          // the edit survived the reload
    expect(within(screen.getByTestId(`mfapolicy-role-${R2.id}`)).getByRole("checkbox")).toBeChecked();
    await confirmSave();
    await waitFor(() => expect(putMfaPolicy).toHaveBeenLastCalledWith({ policy: "roles", roleIds: [R2.id] }, 9));
    expect(await screen.findByTestId("mfapolicy-notice")).toHaveTextContent(A("mfapolicy.saved"));
    // the revision for the NEXT save comes from the save answer (10), not from another GET
    await userEvent.click(kindRadio("off"));
    await userEvent.click(screen.getByTestId("mfapolicy-save"));
    await waitFor(() => expect(putMfaPolicy).toHaveBeenLastCalledWith({ policy: "off", roleIds: [] }, 10));
    expect(client.getMfaPolicy).toHaveBeenCalledTimes(2);
  });

  it("L5: 'Discard changes' after a reloaded conflict shows the policy now in force", async () => {
    const getMfaPolicy = vi.fn().mockResolvedValueOnce(OFF).mockResolvedValue({ policy: "all", roleIds: [], updatedAtUtc: null, revision: 9 });
    setupScreen({ putMfaPolicy: vi.fn().mockRejectedValue(new ApiError(412, "revision_conflict", { currentRevision: 9 })), getMfaPolicy });
    await openByHash();
    await chooseRoles(R2.id);
    await confirmSave();
    await userEvent.click(await screen.findByTestId("mfapolicy-reload"));
    await screen.findByTestId("mfapolicy-reloaded");
    await userEvent.click(screen.getByTestId("mfapolicy-cancel"));
    expect(kindRadio("all")).toBeChecked();
    expect(screen.getByTestId("mfapolicy-save")).toBeDisabled();
  });

  it("L5: 428 precondition_required is surfaced as an application error; the policy is not shown as saved and the choice stays", async () => {
    setupScreen({ putMfaPolicy: vi.fn().mockRejectedValue(new ApiError(428, "precondition_required")) });
    await openByHash();
    await userEvent.click(kindRadio("all"));
    await confirmSave();
    expect(await screen.findByTestId("mfapolicy-error")).toHaveTextContent(A("mfapolicy.error.precondition_required"));
    expect(screen.queryByTestId("mfapolicy-notice")).toBeNull();
    expect(screen.queryByTestId("mfapolicy-conflict")).toBeNull();
    expect(kindRadio("all")).toBeChecked();
  });

  it("M3: a page that hands 403 mfa_enrolment_required to handleError raises the prompt even with NO api-client hook (scripted client, no bridge)", async () => {
    const client = setupScreen({ getMfaPolicy: vi.fn().mockRejectedValue(new ApiError(403, "mfa_enrolment_required")) });
    await screen.findByTestId("company-name");
    window.location.hash = "#/company/mfa-policy";
    fireEvent(window, new HashChangeEvent("hashchange"));
    expect(await screen.findByTestId("enrolment-required")).toBeInTheDocument();
    expect(screen.queryByTestId("mfapolicy-load-error")).toBeNull();
    expect(screen.queryByTestId("notice-expired")).toBeNull();
    expect(client.logout).not.toHaveBeenCalled();
  });

  it("L4: with a client that reports no request scope, a late 403 handed to handleError after the user went to company selection raises no prompt", async () => {
    let refuse!: (e: unknown) => void;
    setupScreen({ getMfaPolicy: vi.fn().mockImplementation(() => new Promise((_, reject) => { refuse = reject; })) });
    await screen.findByTestId("company-name");
    window.location.hash = "#/company/mfa-policy";
    fireEvent(window, new HashChangeEvent("hashchange"));
    await waitFor(() => expect(refuse).toBeDefined());                                                 // the policy request is in flight
    await userEvent.click(screen.getByRole("button", { name: A("nav.change_company") }));
    expect(await screen.findByRole("heading", { name: A("companies.title") })).toBeInTheDocument();
    refuse(new ApiError(403, "mfa_enrolment_required"));
    await new Promise((r) => setTimeout(r, 40));
    expect(screen.queryByTestId("enrolment-required")).toBeNull();
    expect(screen.getByRole("heading", { name: A("companies.title") })).toBeInTheDocument();
  });

  it("the Arabic and English catalogs carry the same policy keys", () => {
    const keys = (c: Record<string, string>) => Object.keys(c).filter((k) => k.startsWith("mfapolicy.") || k.startsWith("enrol_required.") || k === "nav.mfa_policy").sort();
    expect(keys(catalog.ar).length).toBeGreaterThan(38);
    expect(keys(catalog.ar)).toEqual(keys(catalog.en));
  });
});

// ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
// The real api client against a scripted server

const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
const ENROL_REQUIRED = () => json({ error: "mfa_enrolment_required" }, 403);
const STALE = () => json({ error: "mfa_step_up_required" }, 401);
type Reply = Response | (() => Response | Promise<Response>);

interface Server {
  companies: { id: string; nameAr: string; nameEn: string }[];
  perms: string[];
  /** answers per path+method, consumed in order; the last answer repeats */
  routes: Record<string, Reply[]>;
}
/**
 * Renders `<App />` with NO injected client and NO injected bridge: the production wiring of `App.tsx` builds the api client (session bridge, enrolment hook, request scope, step-up) and only the global `fetch` is scripted. Every
 * test below therefore runs through the default wiring.
 */
function startApp(server: Server) {
  const calls: { method: string; path: string; body: unknown; ifMatch: string | undefined; status?: number }[] = [];
  const queues = new Map<string, Reply[]>(Object.entries(server.routes).map(([k, v]) => [k, [...v]]));
  const answer = (key: string): Response | Promise<Response> | null => {
    const q = queues.get(key);
    if (!q || q.length === 0) return null;
    const r = q.length > 1 ? q.shift()! : q[0]!;
    return typeof r === "function" ? r() : r;
  };
  let selected = server.companies[0]!.id;
  const respond = async (entry: (typeof calls)[number]): Promise<Response> => {
    const { path, method } = entry;
    const body = entry.body as { companyId?: string } | undefined;
    const scripted = answer(`${method} ${path}`);
    if (scripted) return scripted;
    if (path === "/api/v1/auth/status") return json({ authenticated: true, user: USER, mfa: { state: "none", verified: false, verifiedAtUtc: null } });
    if (path === "/api/v1/companies") return json({ companies: server.companies });
    if (path === "/api/v1/session/company" && method === "DELETE") return new Response(null, { status: 204 });
    if (path === "/api/v1/session/company") { selected = body?.companyId ?? selected; return json({ companyId: selected }); }
    if (path === "/api/v1/access/me") return json({ companyId: selected, grants: [{ roleId: "r", scope: "company", scopeId: null, permissions: server.perms }] });
    if (path === "/api/v1/company/currencies") return json({ currencies: [], baseCurrency: null, baseCurrencySelected: false, baseCurrencyLock: { locked: false, checksImplemented: false, unimplementedDomains: [] } });
    if (path === "/api/v1/company/currencies/reference") return json({ currencies: [] });
    return json({ error: "not_found" }, 404);
  };
  const fetchMock = vi.fn().mockImplementation(async (url: string, init: RequestInit = {}) => {
    const entry: (typeof calls)[number] = { method: init.method ?? "GET", path: new URL(url).pathname, body: typeof init.body === "string" ? JSON.parse(init.body) : undefined, ifMatch: (init.headers as Record<string, string> | undefined)?.["If-Match"] };
    calls.push(entry);
    const response = await respond(entry);
    entry.status = response.status;
    return response;
  });
  vi.stubGlobal("fetch", fetchMock);
  render(<I18nProvider catalog={catalog}><App /></I18nProvider>);
  const count = (method: string, path: string) => calls.filter((c) => c.method === method && c.path === path);
  return { calls, count };
}
/** A server answer the test releases later (a slow request that is still in flight while the user moves on). */
function deferred() {
  let release!: (r: Response) => void;
  const promise = new Promise<Response>((resolve) => { release = resolve; });
  return { reply: () => promise, release };
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
    // L2: the prompt asks for the status ONCE (restoration + prompt = 2). The provider's state changes during the re-selection must not ask again: the answer would be "enrolled" and flash the administrator warning.
    expect(s.count("GET", "/api/v1/auth/status")).toHaveLength(2);
  });

  it("L2: while the enrolment completes and the company is selected again, the 'ask an administrator' warning never appears", async () => {
    const reselect = deferred();
    let status = 0;
    const s = startApp({
      companies: TWO, perms: ["company.profile.view"],
      routes: {
        "POST /api/v1/session/company": [ENROL_REQUIRED, reselect.reply],
        "POST /api/v1/auth/mfa/enrol/begin": [() => json(SECRET)],
        "POST /api/v1/auth/mfa/enrol/confirm": [() => json({ user: { ...USER, displayName: "Amal (enrolled)" } })],
        "GET /api/v1/access/me": [() => json({ error: "company_not_selected" }, 409), () => json({ companyId: "c2", grants: [{ roleId: "r", scope: "company", scopeId: null, permissions: ["company.profile.view"] }] })],
        // restoration and the prompt see "none"; any LATER question would be answered "enrolled"
        "GET /api/v1/auth/status": [() => json({ authenticated: true, user: USER, mfa: { state: ++status <= 2 ? "none" : "enrolled", verified: false, verifiedAtUtc: null } })],
      },
    });
    await userEvent.click(await screen.findByRole("button", { name: /شركة النور/ }));
    await screen.findByTestId("enrolment-required");
    await enrolFromPrompt();
    await waitFor(() => expect(s.count("POST", "/api/v1/session/company")).toHaveLength(2));           // the re-selection is in flight and the user state already changed
    await new Promise((r) => setTimeout(r, 40));
    expect(screen.getByTestId("enrolment-required")).toBeInTheDocument();
    expect(screen.queryByTestId("enrol-required-unknown")).toBeNull();
    expect(s.count("GET", "/api/v1/auth/status")).toHaveLength(2);
    reselect.release(json({ companyId: "c2" }));
    await screen.findByTestId("company-name");
    expect(s.count("GET", "/api/v1/auth/status")).toHaveLength(2);
  });

  it("L1: a session restored with ONE company already selected: after the enrolment that company is selected and entered (no extra click)", async () => {
    const s = startApp({
      companies: [TWO[0]!], perms: ["company.profile.view"],
      routes: {
        "GET /api/v1/access/me": [ENROL_REQUIRED, () => json({ companyId: "c1", grants: [{ roleId: "r", scope: "company", scopeId: null, permissions: ["company.profile.view"] }] })],
        "POST /api/v1/auth/mfa/enrol/begin": [() => json(SECRET)],
        "POST /api/v1/auth/mfa/enrol/confirm": [() => json({ user: USER })],
      },
    });
    await screen.findByTestId("enrolment-required");
    expect(s.count("POST", "/api/v1/session/company")).toHaveLength(0);
    await enrolFromPrompt();
    expect(await screen.findByTestId("company-name")).toHaveTextContent("شركة الأمل");
    expect(s.count("POST", "/api/v1/session/company").map((c) => c.body)).toEqual([{ companyId: "c1" }]);
    expect(screen.queryByRole("heading", { name: A("companies.title") })).toBeNull();
  });

  it("L1: restored with SEVERAL companies the selected one is not known to the client, so after the enrolment the user chooses (nothing is guessed)", async () => {
    const s = startApp({
      companies: TWO, perms: ["company.profile.view"],
      routes: {
        "GET /api/v1/access/me": [ENROL_REQUIRED, () => json({ companyId: "c2", grants: [{ roleId: "r", scope: "company", scopeId: null, permissions: ["company.profile.view"] }] })],
        "POST /api/v1/auth/mfa/enrol/begin": [() => json(SECRET)],
        "POST /api/v1/auth/mfa/enrol/confirm": [() => json({ user: USER })],
      },
    });
    await screen.findByTestId("enrolment-required");
    await enrolFromPrompt();
    expect(await screen.findByRole("heading", { name: A("companies.title") })).toBeInTheDocument();
    expect(s.count("POST", "/api/v1/session/company")).toHaveLength(0);                                // no company was selected on the user's behalf
    await userEvent.click(screen.getByRole("button", { name: /شركة النور/ }));
    expect(await screen.findByTestId("company-name")).toHaveTextContent("شركة النور");
  });

  it("M3: a 403 on a call whose caller does NOT use handleError (the Currencies page swallows its entitlements failure) still raises the prompt, through the default App wiring alone", async () => {
    const s = startApp({
      companies: [TWO[0]!], perms: ["company.profile.view", "company.currencies.view"],
      routes: { "GET /api/v1/company/entitlements": [ENROL_REQUIRED] },
    });
    await screen.findByTestId("company-name");
    window.location.hash = "#/company/currencies";
    fireEvent(window, new HashChangeEvent("hashchange"));
    expect(await screen.findByTestId("enrolment-required")).toBeInTheDocument();
    // Only the entitlements call is refused, and the page swallows that failure (it never reaches handleError); the calls the page DOES pass to handleError were answered 200. So only the hook can have raised the prompt.
    expect(s.count("GET", "/api/v1/company/currencies").length).toBeGreaterThanOrEqual(1);
    expect(s.count("GET", "/api/v1/company/entitlements").length).toBeGreaterThanOrEqual(1);
    expect(s.calls.filter((c) => c.path !== "/api/v1/company/entitlements" && c.status === 403)).toEqual([]);
    expect(screen.queryByTestId("notice-expired")).toBeNull();
    expect(s.count("POST", "/api/v1/auth/logout")).toHaveLength(0);
  });

  it("L4: a late 403 of the PREVIOUS company's request, answered after the user went to company selection, does not raise the prompt", async () => {
    const slow = deferred();
    const s = startApp({
      companies: TWO, perms: ["company.profile.view", "company.currencies.view"],
      routes: {
        "GET /api/v1/access/me": [() => json({ companyId: "c1", grants: [{ roleId: "r", scope: "company", scopeId: null, permissions: ["company.profile.view", "company.currencies.view"] }] })],
        "GET /api/v1/company/currencies": [slow.reply],
      },
    });
    await screen.findByTestId("company-name");
    window.location.hash = "#/company/currencies";
    fireEvent(window, new HashChangeEvent("hashchange"));
    await waitFor(() => expect(s.count("GET", "/api/v1/company/currencies").length).toBeGreaterThanOrEqual(1));         // in flight
    await userEvent.click(screen.getByRole("button", { name: A("nav.change_company") }));
    expect(await screen.findByRole("heading", { name: A("companies.title") })).toBeInTheDocument();
    slow.release(ENROL_REQUIRED());                                                                    // reaches both the hook and the page's handleError
    await new Promise((r) => setTimeout(r, 40));
    expect(screen.queryByTestId("enrolment-required")).toBeNull();
    expect(screen.getByRole("heading", { name: A("companies.title") })).toBeInTheDocument();
    expect(screen.queryByTestId("notice-expired")).toBeNull();
  });

  it("L4: a late 403 of the previous company's request, answered after the user ENTERED another company, does not raise the prompt there", async () => {
    const slow = deferred();
    const s = startApp({
      companies: TWO, perms: ["company.profile.view", "company.currencies.view"],
      routes: {
        "GET /api/v1/access/me": [() => json({ companyId: "c1", grants: [{ roleId: "r", scope: "company", scopeId: null, permissions: ["company.profile.view", "company.currencies.view"] }] }), () => json({ companyId: "c2", grants: [{ roleId: "r", scope: "company", scopeId: null, permissions: ["company.profile.view"] }] })],
        "GET /api/v1/company/currencies": [slow.reply],
      },
    });
    expect(await screen.findByTestId("company-name")).toHaveTextContent("شركة الأمل");
    window.location.hash = "#/company/currencies";
    fireEvent(window, new HashChangeEvent("hashchange"));
    await waitFor(() => expect(s.count("GET", "/api/v1/company/currencies").length).toBeGreaterThanOrEqual(1));
    await userEvent.click(screen.getByRole("button", { name: A("nav.change_company") }));
    await userEvent.click(await screen.findByRole("button", { name: /شركة النور/ }));
    await waitFor(() => expect(screen.getByTestId("company-name")).toHaveTextContent("شركة النور"));
    slow.release(ENROL_REQUIRED());
    await new Promise((r) => setTimeout(r, 40));
    expect(screen.queryByTestId("enrolment-required")).toBeNull();
    expect(screen.getByTestId("company-name")).toHaveTextContent("شركة النور");
  });

  it("L4 does not hide a refusal of the CURRENT company: a 403 that arrives after a company change and belongs to the new company raises the prompt", async () => {
    const s = startApp({
      companies: TWO, perms: ["company.profile.view", "company.currencies.view"],
      routes: {
        "GET /api/v1/access/me": [() => json({ companyId: "c1", grants: [{ roleId: "r", scope: "company", scopeId: null, permissions: ["company.profile.view", "company.currencies.view"] }] }), () => json({ companyId: "c2", grants: [{ roleId: "r", scope: "company", scopeId: null, permissions: ["company.profile.view", "company.currencies.view"] }] })],
        "GET /api/v1/company/entitlements": [ENROL_REQUIRED],
      },
    });
    await screen.findByTestId("company-name");
    await userEvent.click(screen.getByRole("button", { name: A("nav.change_company") }));
    await userEvent.click(await screen.findByRole("button", { name: /شركة النور/ }));
    await waitFor(() => expect(screen.getByTestId("company-name")).toHaveTextContent("شركة النور"));
    window.location.hash = "#/company/currencies";
    fireEvent(window, new HashChangeEvent("hashchange"));
    expect(await screen.findByTestId("enrolment-required")).toBeInTheDocument();
    expect(s.count("GET", "/api/v1/company/entitlements").length).toBeGreaterThanOrEqual(1);
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
        "PUT /api/v1/access/mfa-policy": [STALE, () => json({ policy: "all", roleIds: [], updatedAtUtc: "2026-11-21T10:00:00Z", revision: 4 }), () => json({ policy: "off", roleIds: [], updatedAtUtc: "2026-11-21T10:01:00Z", revision: 5 })],
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
    expect(puts.map((c) => c.ifMatch)).toEqual(['"3"', '"3"']);                                         // the loaded revision as If-Match, and the SAME one on the step-up retry
    expect(s.count("GET", "/api/v1/access/mfa-policy").map((c) => c.ifMatch)).toEqual([undefined]);
    expect(screen.queryByTestId("notice-expired")).toBeNull();
    // the next save carries the revision of the save ANSWER (4); the policy was not fetched again
    await userEvent.click(kindRadio("off"));
    await userEvent.click(screen.getByTestId("mfapolicy-save"));
    await waitFor(() => expect(s.count("PUT", "/api/v1/access/mfa-policy")).toHaveLength(3));
    expect(s.count("PUT", "/api/v1/access/mfa-policy")[2]!.ifMatch).toBe('"4"');
    expect(s.count("GET", "/api/v1/access/mfa-policy")).toHaveLength(1);
  });

  it("412 revision_conflict from the server: the conflict message, the edit kept, and the reload brings the current revision for the next save", async () => {
    const s = startApp({
      companies: [TWO[0]!], perms: PERMS,
      routes: {
        "GET /api/v1/access/mfa-policy": [() => json(OFF), () => json({ policy: "off", roleIds: [], updatedAtUtc: "2026-11-21T09:00:00Z", revision: 9 })],
        "GET /api/v1/access/roles": [() => json({ roles: [] })],
        "PUT /api/v1/access/mfa-policy": [() => json({ error: "revision_conflict", currentRevision: 9 }, 412), () => json({ policy: "all", roleIds: [], updatedAtUtc: "2026-11-21T10:00:00Z", revision: 10 })],
      },
    });
    await goPolicy();
    await saveAll();
    expect(await screen.findByTestId("mfapolicy-conflict")).toHaveTextContent(A("mfapolicy.conflict"));
    expect(screen.queryByTestId("notice-expired")).toBeNull();
    expect(kindRadio("all")).toBeChecked();
    expect(s.count("PUT", "/api/v1/access/mfa-policy")).toHaveLength(1);                               // not retried by itself
    await userEvent.click(screen.getByTestId("mfapolicy-reload"));
    await screen.findByTestId("mfapolicy-reloaded");
    expect(kindRadio("all")).toBeChecked();
    await userEvent.click(screen.getByTestId("mfapolicy-save"));
    await userEvent.click(within(await screen.findByRole("dialog")).getByRole("button", { name: A("mfapolicy.confirm") }));
    expect(await screen.findByTestId("mfapolicy-notice")).toHaveTextContent(A("mfapolicy.saved"));
    expect(s.count("PUT", "/api/v1/access/mfa-policy").map((c) => c.ifMatch)).toEqual(['"3"', '"9"']);
  });

  it("428 precondition_required from the server is shown as an application error (no sign-out, nothing saved)", async () => {
    startApp({
      companies: [TWO[0]!], perms: PERMS,
      routes: {
        "GET /api/v1/access/mfa-policy": [() => json(OFF)],
        "GET /api/v1/access/roles": [() => json({ roles: [] })],
        "PUT /api/v1/access/mfa-policy": [() => json({ error: "precondition_required" }, 428)],
      },
    });
    await goPolicy();
    await saveAll();
    expect(await screen.findByTestId("mfapolicy-error")).toHaveTextContent(A("mfapolicy.error.precondition_required"));
    expect(screen.queryByTestId("mfapolicy-notice")).toBeNull();
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
  // ---- contract as merged (backend PR 181): first write, invalid If-Match, the 409 context answers, the actor without an authenticator, the lock, keyboard, English LTR

  it("first write: a company with no stored policy reads revision 0 and the save sends If-Match \"0\" (never omitted), then the answer's revision", async () => {
    const s = startApp({
      companies: [TWO[0]!], perms: PERMS,
      routes: {
        "GET /api/v1/access/mfa-policy": [() => json({ policy: "off", roleIds: [], revision: 0, updatedAtUtc: null })],
        "GET /api/v1/access/roles": [() => json({ roles: [] })],
        "PUT /api/v1/access/mfa-policy": [() => json({ policy: "all", roleIds: [], updatedAtUtc: "2026-11-21T10:00:00Z", revision: 1 }), () => json({ policy: "off", roleIds: [], updatedAtUtc: "2026-11-21T10:01:00Z", revision: 2 })],
      },
    });
    await goPolicy();
    await saveAll();
    expect(await screen.findByTestId("mfapolicy-notice")).toHaveTextContent(A("mfapolicy.saved"));
    expect(s.count("PUT", "/api/v1/access/mfa-policy").map((c) => c.ifMatch)).toEqual(['"0"']);
    await userEvent.click(kindRadio("off"));
    await userEvent.click(screen.getByTestId("mfapolicy-save"));
    await waitFor(() => expect(s.count("PUT", "/api/v1/access/mfa-policy")).toHaveLength(2));
    expect(s.count("PUT", "/api/v1/access/mfa-policy")[1]!.ifMatch).toBe('"1"');
  });

  it("400 invalid_if_match is worded as an application error in Arabic and in English; nothing is saved, the choice stays, the session stays", async () => {
    startApp({
      companies: [TWO[0]!], perms: PERMS,
      routes: {
        "GET /api/v1/access/mfa-policy": [() => json(OFF)],
        "GET /api/v1/access/roles": [() => json({ roles: [] })],
        "PUT /api/v1/access/mfa-policy": [() => json({ error: "invalid_if_match" }, 400)],
      },
    });
    await goPolicy();
    await saveAll();
    expect(await screen.findByTestId("mfapolicy-error")).toHaveTextContent(A("mfapolicy.error.invalid_if_match"));
    expect(screen.queryByTestId("mfapolicy-notice")).toBeNull();
    expect(screen.queryByTestId("notice-expired")).toBeNull();
    expect(kindRadio("all")).toBeChecked();
    fireEvent.click(screen.getByRole("button", { name: "English" }));
    await waitFor(() => expect(screen.getByTestId("mfapolicy-error")).toHaveTextContent(E("mfapolicy.error.invalid_if_match")));
  });

  it("a caller without an authenticator: 409 mfa_policy_requires_own_enrolment also for switching the policy OFF; link to the security settings, choice kept, no sign-out, no prompt", async () => {
    startApp({
      companies: [TWO[0]!], perms: PERMS,
      routes: {
        "GET /api/v1/access/mfa-policy": [() => json({ policy: "all", roleIds: [], revision: 6, updatedAtUtc: "2026-11-20T09:00:00Z" })],
        "GET /api/v1/access/roles": [() => json({ roles: [] })],
        "PUT /api/v1/access/mfa-policy": [() => json({ error: "mfa_policy_requires_own_enrolment" }, 409)],
      },
    });
    await goPolicy();
    await userEvent.click(kindRadio("off"));
    await userEvent.click(screen.getByTestId("mfapolicy-save"));                                        // 'off': no confirmation dialog
    const banner = await screen.findByTestId("mfapolicy-own-enrolment");
    expect(banner).toHaveTextContent(A("mfapolicy.error.mfa_policy_requires_own_enrolment"));
    expect(within(banner).getByTestId("mfapolicy-security-link")).toHaveAttribute("href", "#/account/security");
    expect(kindRadio("off")).toBeChecked();
    expect(screen.queryByTestId("enrolment-required")).toBeNull();
    expect(screen.queryByTestId("notice-expired")).toBeNull();
  });

  it("409 company_selection_changed: not an error. The save was not served, no error is shown, the session context is read again, and the same company keeps the input", async () => {
    const s = startApp({
      companies: TWO, perms: PERMS,
      routes: {
        "GET /api/v1/access/mfa-policy": [() => json(OFF)],
        "GET /api/v1/access/roles": [() => json({ roles: [] })],
        "PUT /api/v1/access/mfa-policy": [() => json({ error: "company_selection_changed" }, 409), () => json({ policy: "all", roleIds: [], updatedAtUtc: "2026-11-21T10:00:00Z", revision: 4 })],
      },
    });
    await goPolicy();
    const meBefore = s.count("GET", "/api/v1/access/me").length;
    await saveAll();
    expect(await screen.findByTestId("mfapolicy-context")).toHaveTextContent(A("mfapolicy.context_reloaded"));
    expect(screen.queryByTestId("mfapolicy-error")).toBeNull();
    expect(screen.queryByTestId("notice-expired")).toBeNull();
    await waitFor(() => expect(s.count("GET", "/api/v1/access/me").length).toBe(meBefore + 1));
    expect(kindRadio("all")).toBeChecked();                                                             // same company: the input stays
    await userEvent.click(screen.getByTestId("mfapolicy-save"));
    await userEvent.click(within(await screen.findByRole("dialog")).getByRole("button", { name: A("mfapolicy.confirm") }));
    expect(await screen.findByTestId("mfapolicy-notice")).toHaveTextContent(A("mfapolicy.saved"));
    expect(s.count("PUT", "/api/v1/access/mfa-policy").map((c) => c.ifMatch)).toEqual(['"3"', '"3"']);
  });

  it("409 company_selection_changed when the session now has ANOTHER company: the shell is a new instance, the old company's edit is gone, and the other company's policy is loaded", async () => {
    const s = startApp({
      companies: TWO, perms: PERMS,
      routes: {
        "GET /api/v1/access/mfa-policy": [() => json(OFF), () => json({ policy: "all", roleIds: [], revision: 12, updatedAtUtc: "2026-11-21T09:00:00Z" })],
        "GET /api/v1/access/roles": [() => json({ roles: [] })],
        "PUT /api/v1/access/mfa-policy": [() => json({ error: "company_selection_changed" }, 409)],
        "GET /api/v1/access/me": [() => json({ companyId: "c1", grants: [{ roleId: "r", scope: "company", scopeId: null, permissions: PERMS }] }), () => json({ companyId: "c2", grants: [{ roleId: "r", scope: "company", scopeId: null, permissions: PERMS }] })],
      },
    });
    await goPolicy();
    await userEvent.click(kindRadio("roles"));                                                          // an unsaved edit of company 1
    await userEvent.click(kindRadio("off"));
    await userEvent.click(kindRadio("all"));
    await userEvent.click(screen.getByTestId("mfapolicy-save"));
    await userEvent.click(within(await screen.findByRole("dialog")).getByRole("button", { name: A("mfapolicy.confirm") }));
    await waitFor(() => expect(s.count("GET", "/api/v1/access/mfa-policy")).toHaveLength(2));
    expect(await screen.findByRole("heading", { name: A("mfapolicy.title"), level: 1 })).toBeInTheDocument();
    await waitFor(() => expect(kindRadio("all")).toBeChecked());
    expect(screen.getByTestId("mfapolicy-save")).toBeDisabled();                                        // nothing unsaved: the screen shows company 2's saved policy
    expect(screen.queryByTestId("mfapolicy-error")).toBeNull();
    expect(screen.queryByTestId("notice-expired")).toBeNull();
  });

  it("409 company_not_selected: back to company selection (no error, no sign-out)", async () => {
    startApp({
      companies: TWO, perms: PERMS,
      routes: {
        "GET /api/v1/access/mfa-policy": [() => json(OFF)],
        "GET /api/v1/access/roles": [() => json({ roles: [] })],
        "PUT /api/v1/access/mfa-policy": [() => json({ error: "company_not_selected" }, 409)],
        "GET /api/v1/access/me": [() => json({ companyId: "c1", grants: [{ roleId: "r", scope: "company", scopeId: null, permissions: PERMS }] }), () => json({ error: "company_not_selected" }, 409)],
      },
    });
    await goPolicy();
    await saveAll();
    expect(await screen.findByRole("heading", { name: A("companies.title") })).toBeInTheDocument();
    expect(screen.queryByTestId("notice-expired")).toBeNull();
    expect(screen.queryByTestId("mfapolicy-error")).toBeNull();
  });

  it("English: the screen is left-to-right with English wording; Arabic: right-to-left", async () => {
    setupScreen();
    await openByHash();
    expect(document.documentElement.dir).toBe("rtl");
    fireEvent.click(screen.getByRole("button", { name: "English" }));
    expect(await screen.findByRole("heading", { name: E("mfapolicy.title"), level: 1 })).toBeInTheDocument();
    expect(document.documentElement.dir).toBe("ltr");
    expect(screen.getByText(E("mfapolicy.kind.all"))).toBeInTheDocument();
    expect(screen.getByLabelText(new RegExp(E("mfapolicy.kind.off")))).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "العربية" }));
    await screen.findByRole("heading", { name: A("mfapolicy.title"), level: 1 });
    expect(document.documentElement.dir).toBe("rtl");
  });

  it("keyboard: the kinds are one radio group (arrows move the choice), Tab reaches Save, Enter opens the confirmation, Escape-free: the confirm button saves", async () => {
    const client = setupScreen();
    await openByHash();
    kindRadio("off").focus();
    await userEvent.keyboard("{ArrowDown}");                                                           // off -> roles
    await userEvent.keyboard("{ArrowDown}");                                                           // roles -> all
    expect(kindRadio("all")).toBeChecked();
    expect(kindRadio("all")).toHaveFocus();
    await userEvent.tab();
    expect(screen.getByTestId("mfapolicy-save")).toHaveFocus();
    await userEvent.keyboard("{Enter}");
    const dialog = await screen.findByRole("dialog");
    await userEvent.click(within(dialog).getByRole("button", { name: A("mfapolicy.confirm") }));
    await waitFor(() => expect(client.putMfaPolicy).toHaveBeenCalledWith({ policy: "all", roleIds: [] }, 3));
  });

  it("D-236: an unsaved policy choice survives a session end (locked, hidden), is restored after the same user signs in again, nothing is stored, and the save is not re-sent by itself", async () => {
    const client = setupScreen({ putMfaPolicy: vi.fn().mockRejectedValue(new ApiError(401, "unauthenticated")) });
    const writes = vi.spyOn(Storage.prototype, "setItem");
    await openByHash();
    await userEvent.click(kindRadio("roles"));
    await userEvent.click(within(screen.getByTestId(`mfapolicy-role-${R2.id}`)).getByRole("checkbox"));
    await userEvent.click(screen.getByTestId("mfapolicy-save"));
    await userEvent.click(within(await screen.findByRole("dialog")).getByRole("button", { name: A("mfapolicy.confirm") }));
    await screen.findByTestId("reauth-dialog");
    expect(screen.getByTestId("shell-root")).toHaveAttribute("hidden");
    expect(screen.getByTestId("shell-root")).toHaveAttribute("inert");
    const dialog = within(screen.getByTestId("reauth-dialog"));
    await userEvent.type(dialog.getByLabelText(/اسم المستخدم/), "amal");
    await userEvent.type(dialog.getByLabelText(/كلمة المرور/), "secret-password");
    await userEvent.click(dialog.getByRole("button", { name: "دخول" }));
    await waitFor(() => expect(screen.queryByTestId("reauth-dialog")).toBeNull());
    expect(kindRadio("roles")).toBeChecked();
    expect(within(screen.getByTestId(`mfapolicy-role-${R2.id}`)).getByRole("checkbox")).toBeChecked();
    expect(client.putMfaPolicy).toHaveBeenCalledTimes(1);
    for (const c of writes.mock.calls) expect(String(c[0])).toMatch(/lang|locale/i);
    writes.mockRestore();
  });
});

describe("a refused company is not selected again after the user went elsewhere (review M1 of PR 185)", () => {
  it("after 'choose another company' and entering c1, a later voluntary enrolment keeps c1: the refused c2 is never selected again", async () => {
    const s = startApp({
      companies: TWO, perms: ["company.profile.view"],
      routes: {
        "POST /api/v1/session/company": [ENROL_REQUIRED, () => json({ companyId: "c1" })],                 // c2 refuses, c1 does not
        "POST /api/v1/auth/mfa/enrol/begin": [() => json(SECRET)],
        "POST /api/v1/auth/mfa/enrol/confirm": [() => json({ user: USER })],
        "GET /api/v1/access/me": [() => json({ error: "company_not_selected" }, 409), () => json({ companyId: "c1", grants: [{ roleId: "r", scope: "company", scopeId: null, permissions: ["company.profile.view"] }] })],
      },
    });
    await userEvent.click(await screen.findByRole("button", { name: /شركة النور/ }));
    expect(await screen.findByTestId("enrolment-required")).toBeInTheDocument();
    await userEvent.click(screen.getByTestId("enrol-required-another"));
    await userEvent.click(await screen.findByRole("button", { name: /شركة الأمل/ }));
    expect(await screen.findByTestId("company-name")).toHaveTextContent("شركة الأمل");
    window.location.hash = "#/account/security";                                                           // a voluntary enrolment from the Security screen of company 1
    fireEvent(window, new HashChangeEvent("hashchange"));
    await enrolFromPrompt();
    await waitFor(() => expect(s.count("GET", "/api/v1/access/me").length).toBeGreaterThanOrEqual(2));
    await waitFor(() => expect(screen.getByTestId("company-name")).toHaveTextContent("شركة الأمل"));
    const selections = s.count("POST", "/api/v1/session/company").map((c) => (c.body as { companyId: string }).companyId);
    expect(selections).toEqual(["c2", "c1", "c1"]);                                                         // the enrolment re-enters the CURRENT company, never the stale refusal
  });
});
