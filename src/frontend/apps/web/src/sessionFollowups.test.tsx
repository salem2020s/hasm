import { ApiError, createSessionGate } from "@hasm/api-client";
import { I18nProvider } from "@hasm/ui";
import { act, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { AuthProvider, createSessionBridge, useAuth } from "./auth";
import { catalog } from "./messages";
import { signedIn, USER } from "./test-utils";
import { UnsavedProvider, useReportUnsaved } from "./unsaved";

/**
 * Review follow-ups of PR 185 and PR 196 on the session provider: the resume of a locked shell treats a context 409 of access/me as a retry (L1 of PR 196), the previous user's company list is gone after a drop (L2 of PR 196), the
 * context reload re-checks the lock on its 409 branch (L3 of PR 185), and the enrolment adopts a changed company as a new shell (L2 of PR 185). The provider is exercised directly with a probe that exposes the auth value.
 */
beforeEach(() => { localStorage.clear(); sessionStorage.clear(); window.location.hash = ""; });
afterEach(() => { vi.restoreAllMocks(); });

const PERMS = ["company.profile.view", "company.profile.update"];
const me = (companyId: string) => ({ companyId, grants: [{ roleId: "r", scope: "company", scopeId: null, permissions: PERMS }] });
const OTHER = { id: "u2", login: "badr", displayName: "Badr", isIdentityAdministrator: false };
const TWO = [{ id: "c1", nameAr: "أ", nameEn: "A" }, { id: "c2", nameAr: "ب", nameEn: "B" }];

let auth: ReturnType<typeof useAuth>;
function Probe() {
  auth = useAuth();
  return <span data-testid="probe">{auth.phase}|{String(auth.locked)}|{auth.companyId ?? "-"}|{auth.notice ?? "-"}|{auth.shellEpoch}|{auth.companies.length}</span>;
}
function Dirty() { useReportUnsaved("probe-screen", true, "probe"); return <input data-testid="typed" defaultValue="unsaved" />; }
const state = () => (screen.getByTestId("probe").textContent ?? "").split("|");   // phase, locked, company, notice, epoch, companies

async function start(overrides: Record<string, unknown> = {}, dirty = true) {
  const gate = createSessionGate();
  const bridge = createSessionBridge();
  const client = signedIn({ listCompanies: vi.fn().mockResolvedValue({ companies: [TWO[0]] }), ...overrides }, PERMS);
  render(<I18nProvider catalog={catalog}><AuthProvider api={client} bridge={bridge} gate={gate}><UnsavedProvider><Probe />{dirty && <Dirty />}</UnsavedProvider></AuthProvider></I18nProvider>);
  await waitFor(() => expect(state()[0]).toBe("ready"));
  return { gate, bridge, client };
}
const lock = async () => { act(() => { auth.handleError(new ApiError(401, "unauthenticated")); }); await waitFor(() => expect(state()[1]).toBe("true")); };

describe("resuming a locked shell: a context 409 of access/me is a retry, not a refusal (PR 196 L1)", () => {
  for (const code of ["company_not_selected", "company_selection_changed"]) {
    it(`${code}: the lock, the input and the shell stay, the retry message is shown, and signing in again can still resume`, async () => {
      const accessMe = vi.fn().mockResolvedValueOnce(me("c1")).mockRejectedValueOnce(new ApiError(409, code)).mockResolvedValue(me("c1"));
      const { client } = await start({ accessMe });
      await lock();
      await act(async () => { await auth.login("amal", "pw"); });
      expect(state().slice(0, 4)).toEqual(["ready", "true", "c1", "resume_failed"]);                // still locked, same company, the retry notice
      expect(screen.getByTestId("typed")).toHaveValue("unsaved");                                    // never dropped
      expect(client.listCompanies).toHaveBeenCalledTimes(1);                                         // no fresh company list: the shell was not rebuilt
      await act(async () => { await auth.login("amal", "pw"); });                                    // the retry works
      await waitFor(() => expect(state().slice(0, 4)).toEqual(["ready", "false", "c1", "-"]));
      expect(screen.getByTestId("typed")).toHaveValue("unsaved");
    });
  }

  it("a definite answer still drops: access/me naming ANOTHER company ends the preserved input (the shell becomes a new instance)", async () => {
    const other = vi.fn().mockResolvedValueOnce(me("c1")).mockResolvedValueOnce(me("c2"));
    await start({ accessMe: other });
    await lock();
    const epoch = Number(state()[4]);
    await act(async () => { await auth.login("amal", "pw"); });
    await waitFor(() => expect(Number(state()[4])).toBeGreaterThan(epoch));                          // dropped: the shell became a new instance (the probe has no shell to unmount)
    expect(state()[3]).not.toBe("resume_failed");
  });
});

describe("dropping the shell clears the previous user's company list (PR 196 L2)", () => {
  it("no stale list is held or rendered while the next user's list is still loading", async () => {
    let answer: (v: unknown) => void = () => undefined;
    const listCompanies = vi.fn().mockResolvedValueOnce({ companies: TWO }).mockImplementation(() => new Promise((resolve) => { answer = resolve; }));
    await start({ listCompanies, accessMe: vi.fn().mockResolvedValue(me("c1")) });
    await waitFor(() => expect(state()[5]).toBe("2"));                                               // the first user's two companies
    await lock();
    const login = vi.fn().mockResolvedValue({ user: OTHER });
    (auth.api as unknown as { login: typeof login }).login = login;
    await act(async () => { void auth.login("badr", "pw"); await Promise.resolve(); await Promise.resolve(); });   // a different user signs in: the shell is dropped, the new list is awaited
    await waitFor(() => expect(state()[0]).toBe("loading"));
    expect(state()[5]).toBe("0");                                                                    // nothing of the previous user's list
    await act(async () => { answer({ companies: [] }); await Promise.resolve(); });
  });
});

describe("the context reload and the lock on its 409 branch (PR 185 L3)", () => {
  it("a session that locks while access/me is in flight and then answers 409 company_not_selected is not sent to company selection and keeps the hidden shell", async () => {
    let fail: (e: unknown) => void = () => undefined;
    const accessMe = vi.fn().mockResolvedValueOnce(me("c1"));
    const { client } = await start({ accessMe });
    accessMe.mockImplementation(() => new Promise((_, reject) => { fail = reject; }));
    act(() => { auth.handleError(new ApiError(409, "company_selection_changed")); });
    await waitFor(() => expect(accessMe).toHaveBeenCalledTimes(2));
    await lock();
    await act(async () => { fail(new ApiError(409, "company_not_selected")); await Promise.resolve(); await Promise.resolve(); });
    expect(state().slice(0, 3)).toEqual(["ready", "true", "c1"]);                                    // not "needCompany", company kept
    expect(screen.getByTestId("typed")).toHaveValue("unsaved");
    expect(client.listCompanies).toHaveBeenCalledTimes(1);
  });
});

describe("the enrolment adopts a changed company as a new shell (PR 185 L2)", () => {
  it("a refusal of company c2 followed by the enrolment selects c2 in a NEW shell instance (the epoch advances); the same company does not advance it", async () => {
    const selectCompany = vi.fn().mockImplementation(async (id: string) => { if (id === "c2" && refuse) throw new ApiError(403, "mfa_enrolment_required"); return { companyId: id }; });
    let refuse = true;
    const accessMe = vi.fn().mockRejectedValue(new ApiError(409, "company_not_selected"));
    const gate = createSessionGate();
    const client = signedIn({ selectCompany, accessMe, listCompanies: vi.fn().mockResolvedValue({ companies: TWO }) }, PERMS);
    render(<I18nProvider catalog={catalog}><AuthProvider api={client} gate={gate}><UnsavedProvider><Probe /></UnsavedProvider></AuthProvider></I18nProvider>);
    await waitFor(() => expect(state()[0]).toBe("needCompany"));
    await act(async () => { await auth.chooseCompany("c2"); });                                       // c2 refuses: the enrolment prompt, c2 remembered
    await waitFor(() => expect(state()[0]).toBe("needEnrolment"));
    const before = Number(state()[4]);
    refuse = false;
    accessMe.mockReset(); accessMe.mockResolvedValue(me("c2"));
    await act(async () => { await auth.adoptEnrolledSession(USER); });
    await waitFor(() => expect(state().slice(0, 3)).toEqual(["ready", "false", "c2"]));
    expect(Number(state()[4])).toBeGreaterThan(before);                                              // a company the user was not in: a new shell
    // the same company again (enrolment from inside c2): no new shell
    const inside = Number(state()[4]);
    act(() => { auth.handleError(new ApiError(403, "mfa_enrolment_required", { scope: undefined })); });
    await waitFor(() => expect(state()[0]).toBe("needEnrolment"));
    await act(async () => { await auth.adoptEnrolledSession(USER); });
    await waitFor(() => expect(state().slice(0, 3)).toEqual(["ready", "false", "c2"]));
    expect(Number(state()[4])).toBe(inside);
  });
});
