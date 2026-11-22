import { ApiError, createSessionGate } from "@hasm/api-client";
import { I18nProvider } from "@hasm/ui";
import { act, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { AuthProvider, createSessionBridge, useAuth } from "./auth";
import { catalog } from "./messages";
import { signedIn } from "./test-utils";
import { UnsavedProvider, useReportUnsaved } from "./unsaved";

/**
 * The session guards around the enrolment signal and the company-context reload (review L1 and L2 of PR 185): while the shell is LOCKED (D-236) neither an enrolment-required signal nor a late answer of the context reload may touch it.
 * The provider is exercised directly with a probe that exposes the auth value, and a screen that reports unsaved input (which is what makes a 401 lock instead of signing out).
 */
beforeEach(() => { localStorage.clear(); sessionStorage.clear(); window.location.hash = ""; });
afterEach(() => { vi.restoreAllMocks(); });

const C2 = "c2";
let auth: ReturnType<typeof useAuth>;
function Probe() {
  auth = useAuth();
  return <span data-testid="probe">{auth.phase}:{String(auth.locked)}:{auth.companyId ?? "-"}:{auth.can("company.profile.update") ? "update" : "-"}</span>;
}
function Dirty() { useReportUnsaved("probe-screen", true, "probe"); return <input data-testid="typed" defaultValue="unsaved" />; }

async function start(overrides: Record<string, unknown> = {}, dirty = true) {
  const gate = createSessionGate();
  const bridge = createSessionBridge();
  const client = signedIn(overrides, ["company.profile.view", "company.profile.update"]);
  render(<I18nProvider catalog={catalog}><AuthProvider api={client} bridge={bridge} gate={gate}><UnsavedProvider><Probe />{dirty && <Dirty />}</UnsavedProvider></AuthProvider></I18nProvider>);
  await waitFor(() => expect(screen.getByTestId("probe")).toHaveTextContent(/^ready:false:c1/));
  return { gate, bridge, client };
}
const state = () => screen.getByTestId("probe").textContent;

describe("an enrolment-required signal and the locked shell (L1)", () => {
  it("is ignored while the shell is locked: the phase stays ready, the shell stays hidden behind the lock and the gate is never reopened", async () => {
    const { gate, bridge } = await start();
    act(() => { auth.handleError(new ApiError(401, "unauthenticated")); });                   // unsaved input: the 401 LOCKS instead of signing out
    await waitFor(() => expect(state()).toMatch(/^ready:true:c1/));
    expect(gate.isOpen()).toBe(false);
    act(() => { bridge.fireEnrolment(bridge.scope()); });                                      // the hook of the api client: a refusal that arrives while locked
    act(() => { auth.handleError(new ApiError(403, "mfa_enrolment_required", { scope: bridge.scope() })); });   // and the page-level route of the same signal
    expect(state()).toMatch(/^ready:true:c1/);                                                  // not "needEnrolment"
    expect(gate.isOpen()).toBe(false);
    expect(screen.getByTestId("typed")).toHaveValue("unsaved");                                 // the preserved input is untouched
  });

  it("is honoured when the shell is not locked (the guard is the lock, not the signal)", async () => {
    const { gate, bridge } = await start({}, false);
    act(() => { bridge.fireEnrolment(bridge.scope()); });
    await waitFor(() => expect(state()).toMatch(/^needEnrolment:false:c1/));
    expect(gate.isOpen()).toBe(true);                                                           // the prompt is an authenticated screen without a company: it can talk
  });
});

describe("the company-context reload and the lock (L2)", () => {
  it("a session that locks while access/me is in flight keeps the hidden shell and its input: a late answer naming another company changes nothing", async () => {
    let answer: (v: unknown) => void = () => undefined;
    const accessMe = vi.fn().mockResolvedValueOnce({ companyId: "c1", grants: [{ roleId: "r", scope: "company", scopeId: null, permissions: ["company.profile.view", "company.profile.update"] }] });
    const { client } = await start({ accessMe });
    accessMe.mockImplementation(() => new Promise((resolve) => { answer = resolve; }));
    act(() => { auth.handleError(new ApiError(409, "company_selection_changed")); });          // the reload starts and asks the server
    await waitFor(() => expect(accessMe).toHaveBeenCalledTimes(2));
    act(() => { auth.handleError(new ApiError(401, "unauthenticated")); });                   // the session ends meanwhile: locked, input kept
    await waitFor(() => expect(state()).toMatch(/^ready:true:c1/));
    await act(async () => { answer({ companyId: C2, grants: [{ roleId: "r", scope: "company", scopeId: null, permissions: [] }] }); await Promise.resolve(); });
    expect(state()).toMatch(/^ready:true:c1:update/);                                           // still company 1 with its permissions: the answer was dropped
    expect(screen.getByTestId("typed")).toHaveValue("unsaved");
    expect(client.listCompanies).toHaveBeenCalledTimes(1);                                      // and it did not send the user to company selection either
  });

  it("is not started while locked or outside the ready phase", async () => {
    const accessMe = vi.fn().mockResolvedValue({ companyId: "c1", grants: [{ roleId: "r", scope: "company", scopeId: null, permissions: ["company.profile.view"] }] });
    await start({ accessMe });
    act(() => { auth.handleError(new ApiError(401, "unauthenticated")); });
    await waitFor(() => expect(state()).toMatch(/^ready:true/));
    const calls = accessMe.mock.calls.length;
    act(() => { auth.handleError(new ApiError(409, "company_selection_changed")); });          // locked: ignored
    await act(async () => { await Promise.resolve(); });
    expect(accessMe.mock.calls.length).toBe(calls);
  });

  it("is not started from company selection (phase guard): a late 409 there asks nothing", async () => {
    const gate = createSessionGate();
    const accessMe = vi.fn().mockRejectedValue(new ApiError(409, "company_not_selected"));
    const client = signedIn({ accessMe, listCompanies: vi.fn().mockResolvedValue({ companies: [{ id: "c1", nameAr: "أ", nameEn: "A" }, { id: C2, nameAr: "ب", nameEn: "B" }] }) });
    render(<I18nProvider catalog={catalog}><AuthProvider api={client} gate={gate}><UnsavedProvider><Probe /></UnsavedProvider></AuthProvider></I18nProvider>);
    await waitFor(() => expect(state()).toMatch(/^needCompany/));
    const calls = accessMe.mock.calls.length;
    act(() => { expect(auth.handleError(new ApiError(409, "company_selection_changed"))).toBe(true); });   // handled (never an error to show), but nothing is rebuilt
    await act(async () => { await Promise.resolve(); });
    expect(accessMe.mock.calls.length).toBe(calls);
    expect(state()).toMatch(/^needCompany/);
  });
});
