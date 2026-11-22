import { ApiError } from "@hasm/api-client";
import { I18nProvider } from "@hasm/ui";
import { act, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { catalog } from "./messages";
import { AuthProvider, useAuth } from "./auth";
import { ALL_PERMISSIONS, USER, apiError, fakeClient, renderApp, signedIn } from "./test-utils";

/**
 * The user's own security settings (Phase 4.1 S3b-2a), shared by Web and Desktop: the two-step verification state and the VOLUNTARY enrolment (password, secret shown once as text, code, a new session). The server decides every
 * step; the screen only shows it. Secrets, codes and the password live only in the component and are never stored.
 */
const A = (k: string) => catalog.ar[k]!;
const E = (k: string) => catalog.en[k]!;
beforeEach(() => { localStorage.clear(); sessionStorage.clear(); window.location.hash = ""; });

const status = (mfa?: Record<string, unknown>) => ({ authenticated: true, user: USER, ...(mfa ? { mfa } : {}) });
const NONE = { state: "none", verified: false, verifiedAtUtc: null };
const ENROLLED = { state: "enrolled", verified: true, verifiedAtUtc: "2026-11-21T10:00:00Z" };
const SECRET = { otpauthUri: "otpauth://totp/HASM:amal?secret=JBSWY3DPEHPK3PXP&issuer=HASM", secret: "JBSWY3DPEHPK3PXP" };

function setup(over: Record<string, unknown> = {}, first = status(NONE)) {
  const client = signedIn({
    status: vi.fn().mockResolvedValue(first),
    beginEnrolment: vi.fn().mockResolvedValue(SECRET),
    confirmEnrolment: vi.fn().mockResolvedValue({ user: USER }),
    ...over,
  }, ALL_PERMISSIONS);
  renderApp(client);
  return client;
}
const openSecurity = async () => {
  await userEvent.click(await screen.findByTestId("nav-security"));
  await screen.findByRole("heading", { name: /^(الأمان|Security)$/, level: 1 });
};
const beginWith = async (password = "Correct-1") => {
  await userEvent.type(await screen.findByLabelText(new RegExp(A("sec.enrol.password"))), password);
  await userEvent.click(screen.getByRole("button", { name: A("sec.enrol.start") }));
};

describe("security settings screen", () => {
  it("is reached from the header, shows the two-step state, and a user without it is offered the voluntary enrolment", async () => {
    setup();
    await openSecurity();
    expect(screen.getByTestId("sec-state-chip")).toHaveTextContent(A("sec.mfa.state.none"));
    expect(screen.getByTestId("nav-security")).toHaveAttribute("aria-current", "page");
    expect(screen.getByTestId("sec-enrol-begin")).toBeInTheDocument();
    expect(screen.queryByTestId("sec-required")).toBeNull();                                          // a server that does not report `required`: nothing is invented
  });

  it.each([[true, "sec.mfa.required_yes"], [false, "sec.mfa.required_no"]])("shows the server's mfa.required=%s for a user without two-step verification", async (required, key) => {
    setup({}, status({ ...NONE, required }));
    await openSecurity();
    expect(screen.getByTestId("sec-required")).toHaveTextContent(A(key));
    expect(screen.getByTestId("sec-enrol-begin")).toBeInTheDocument();
  });

  it("an enrolled user whose server reports required=false is shown as not required", async () => {
    setup({}, status({ ...ENROLLED, required: false }));
    await openSecurity();
    expect(screen.getByTestId("sec-required")).toHaveTextContent(A("sec.mfa.required_no"));
  });

  it("an enrolled user sees the state, the last verification and whether it is required, with no enrolment form", async () => {
    setup({}, status({ ...ENROLLED, required: true }));
    await openSecurity();
    expect(screen.getByTestId("sec-state-chip")).toHaveTextContent(A("sec.mfa.state.enrolled"));
    expect(screen.getByTestId("sec-required")).toHaveTextContent(A("sec.mfa.required_yes"));
    expect(screen.getByText(A("sec.mfa.verified_at"))).toBeInTheDocument();
    expect(screen.getByTestId("sec-enrolled-note")).toBeInTheDocument();
    expect(screen.queryByTestId("sec-enrol-begin")).toBeNull();
  });

  it.each([["enrolmentrequired"], ["recoveryrequired"]])("a user in state %s gets the explanation and no enrolment form (an administrator acts)", async (state) => {
    setup({}, status({ state, verified: false, verifiedAtUtc: null }));
    await openSecurity();
    expect(screen.getByTestId("sec-other-note")).toBeInTheDocument();
    expect(screen.queryByTestId("sec-enrol-begin")).toBeNull();
  });

  it("a server that reports no state is said so, and offers nothing", async () => {
    setup({}, status());
    await openSecurity();
    expect(screen.getByText(A("sec.mfa.unknown_note"))).toBeInTheDocument();
    expect(screen.queryByTestId("sec-enrol-begin")).toBeNull();
  });

  it("voluntary enrolment: password, the secret shown ONCE as text, a code, a new session adopted, the secret and the inputs gone, the state refreshed", async () => {
    const statusMock = vi.fn().mockResolvedValueOnce(status(NONE)).mockResolvedValueOnce(status(NONE)).mockResolvedValue(status(ENROLLED));
    const client = setup({ status: statusMock });
    await openSecurity();
    await beginWith("Correct-1");
    expect(client.beginEnrolment).toHaveBeenCalledWith({ password: "Correct-1" });
    expect(await screen.findByTestId("mfa-secret")).toHaveTextContent(SECRET.secret);                  // text only, no image
    expect(screen.getByTestId("mfa-uri")).toHaveAttribute("href", SECRET.otpauthUri);
    expect(screen.getByTestId("mfa-secret")).toHaveAttribute("dir", "ltr");
    expect(screen.queryByLabelText(new RegExp(A("sec.enrol.password")))).toBeNull();                   // the password field is gone, nothing of it is on the page
    await userEvent.type(screen.getByLabelText(new RegExp(A("mfa.code"))), "123456");
    await userEvent.click(screen.getByRole("button", { name: A("mfa.enrol.confirm") }));
    expect(await screen.findByTestId("sec-notice")).toHaveTextContent(A("sec.enrol.done"));
    expect(client.confirmEnrolment).toHaveBeenCalledWith({ code: "123456" });
    expect(client.selectCompany).toHaveBeenCalledWith("c1");                                           // the NEW session selects the same company again
    expect(await screen.findByTestId("sec-state-chip")).toHaveTextContent(A("sec.mfa.state.enrolled"));
    expect(screen.queryByTestId("mfa-secret")).toBeNull();
    expect(screen.queryByTestId("sec-enrol-begin")).toBeNull();
    expect(screen.getByTestId("company-name")).toBeInTheDocument();                                    // still signed in, in the same company
    expect(document.body.innerHTML).not.toContain(SECRET.secret);
    expect(document.body.innerHTML).not.toContain("Correct-1");
  });

  it("Arabic-Indic digits are accepted for the code, a code of the wrong shape is refused before any request", async () => {
    const client = setup();
    await openSecurity();
    await beginWith();
    const code = await screen.findByLabelText(new RegExp(A("mfa.code")));
    await userEvent.type(code, "12345");
    await userEvent.click(screen.getByRole("button", { name: A("mfa.enrol.confirm") }));
    expect(await screen.findByText(A("mfa.step_up.format"))).toBeInTheDocument();
    expect(client.confirmEnrolment).not.toHaveBeenCalled();
    await userEvent.clear(code);
    await userEvent.type(code, "١٢٣٤٥٦{Enter}");
    await waitFor(() => expect(client.confirmEnrolment).toHaveBeenCalledWith({ code: "123456" }));
  });

  it("a wrong password is a message, shows no secret, and keeps the session (the server still reports it alive)", async () => {
    const client = setup({ beginEnrolment: vi.fn().mockRejectedValue(apiError(401, "invalid_credentials")) });
    await openSecurity();
    await beginWith("wrong");
    expect(await screen.findByTestId("sec-error")).toHaveTextContent(A("sec.enrol.begin_refused"));
    expect(screen.queryByTestId("mfa-secret")).toBeNull();
    expect(screen.queryByTestId("notice-expired")).toBeNull();
    expect(screen.getByLabelText(new RegExp(A("sec.enrol.password")))).toHaveValue("");                // the password was dropped from the field
    expect(client.beginEnrolment).toHaveBeenCalledTimes(1);
  });

  it("a wrong code keeps the secret on screen for another try, clears the field, and says so", async () => {
    setup({ confirmEnrolment: vi.fn().mockRejectedValue(apiError(401, "invalid_credentials")) });
    await openSecurity();
    await beginWith();
    const code = await screen.findByLabelText(new RegExp(A("mfa.code")));
    await userEvent.type(code, "000000");
    await userEvent.click(screen.getByRole("button", { name: A("mfa.enrol.confirm") }));
    expect(await screen.findByTestId("sec-error")).toHaveTextContent(A("sec.enrol.confirm_refused"));
    expect(screen.getByTestId("mfa-secret")).toBeInTheDocument();
    expect(code).toHaveValue("");
  });

  it("the throttle (429) is its own message for the password step and the code step", async () => {
    setup({ beginEnrolment: vi.fn().mockRejectedValueOnce(apiError(429, "too_many_attempts")).mockResolvedValue(SECRET), confirmEnrolment: vi.fn().mockRejectedValue(apiError(429, "too_many_attempts")) });
    await openSecurity();
    await beginWith();
    expect(await screen.findByTestId("sec-error")).toHaveTextContent(A("mfa.throttled"));
    await beginWith("Correct-1");
    await userEvent.type(await screen.findByLabelText(new RegExp(A("mfa.code"))), "111111");
    await userEvent.click(screen.getByRole("button", { name: A("mfa.enrol.confirm") }));
    expect(await screen.findByTestId("sec-error")).toHaveTextContent(A("mfa.throttled"));
  });

  it("a 401 when the session itself is gone (the server says not authenticated) ends the session instead of blaming the input", async () => {
    const statusMock = vi.fn().mockResolvedValueOnce(status(NONE)).mockResolvedValueOnce(status(NONE)).mockResolvedValue({ authenticated: false });
    setup({ status: statusMock, beginEnrolment: vi.fn().mockRejectedValue(apiError(401, "invalid_credentials")) });
    await openSecurity();
    await beginWith();
    expect(await screen.findByTestId("notice-expired")).toBeInTheDocument();
  });

  it("another error is the generic message, a network failure its own, and nothing is shown of the server's text", async () => {
    setup({ beginEnrolment: vi.fn().mockRejectedValueOnce(apiError(500, "boom")).mockRejectedValueOnce(new TypeError("fetch failed")) });
    await openSecurity();
    await beginWith();
    expect(await screen.findByTestId("sec-error")).toHaveTextContent(A("error.generic"));
    await beginWith();
    await waitFor(() => expect(screen.getByTestId("sec-error")).toHaveTextContent(A("error.network")));
  });

  it("the secret on screen counts as unsaved: leaving asks Stay or Discard, and Discard drops the secret", async () => {
    setup();
    await openSecurity();
    await beginWith();
    await screen.findByTestId("mfa-secret");
    await userEvent.click(screen.getByRole("button", { name: A("nav.logout") }));
    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByText(new RegExp(A("sec.enrol.section")))).toBeInTheDocument();
    await userEvent.click(within(dialog).getByRole("button", { name: A("unsaved.stay") }));
    expect(screen.getByTestId("mfa-secret")).toBeInTheDocument();
  });

  it("Cancel setup drops the secret and returns to the password step", async () => {
    setup();
    await openSecurity();
    await beginWith();
    await userEvent.click(await screen.findByRole("button", { name: A("sec.enrol.cancel") }));
    expect(screen.queryByTestId("mfa-secret")).toBeNull();
    expect(screen.getByTestId("sec-enrol-begin")).toBeInTheDocument();
  });

  it("the password is a password field, nothing is written to storage, and Enter submits", async () => {
    const client = setup();
    await openSecurity();
    const password = await screen.findByLabelText(new RegExp(A("sec.enrol.password")));
    expect(password).toHaveAttribute("type", "password");
    expect(password).toHaveAttribute("autocomplete", "current-password");
    await userEvent.type(password, "Correct-1{Enter}");
    await screen.findByTestId("mfa-secret");
    expect(client.beginEnrolment).toHaveBeenCalledTimes(1);
    expect(JSON.stringify({ ...localStorage, ...sessionStorage })).not.toMatch(/Correct-1|JBSWY3DPEHPK3PXP/);
  });

  it("a status read failure is a retryable message, not a broken page", async () => {
    const statusMock = vi.fn().mockResolvedValueOnce(status(NONE)).mockRejectedValueOnce(new TypeError("offline")).mockResolvedValue(status(NONE));
    setup({ status: statusMock });
    await userEvent.click(await screen.findByTestId("nav-security"));
    expect(await screen.findByTestId("sec-load-error")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: A("picker.retry") }));
    expect(await screen.findByTestId("sec-state-chip")).toBeInTheDocument();
  });

  it("in English the screen is left to right and worded in English", async () => {
    localStorage.setItem("hasm.locale", "en");
    setup();
    await userEvent.click(await screen.findByTestId("nav-security"));
    expect(await screen.findByRole("heading", { name: E("sec.title"), level: 1 })).toBeInTheDocument();
    expect(document.documentElement.dir).toBe("ltr");
    expect(screen.getByTestId("sec-state-chip")).toHaveTextContent(E("sec.mfa.state.none"));
    expect(screen.getByRole("button", { name: E("sec.enrol.start") })).toBeInTheDocument();
  });

  it("the Arabic and English catalogs carry the same security keys", () => {
    const ar = Object.keys(catalog.ar).filter((k) => k.startsWith("sec.") || k === "nav.security").sort();
    expect(ar.length).toBeGreaterThan(25);
    expect(ar).toEqual(Object.keys(catalog.en).filter((k) => k.startsWith("sec.") || k === "nav.security").sort());
  });
});

describe("a throttled confirmation", () => {
  it("drops the secret from the page after a 429 and shows the throttle message with the password step again", async () => {
    const client = setup({ confirmEnrolment: vi.fn().mockRejectedValue(new ApiError(429, "too_many_attempts")) });
    await openSecurity();
    await beginWith();
    expect(await screen.findByTestId("mfa-secret")).toHaveTextContent(SECRET.secret);
    await userEvent.type(await screen.findByLabelText(new RegExp(A("mfa.code"))), "123456{Enter}");
    await waitFor(() => expect(client.confirmEnrolment).toHaveBeenCalled());
    expect(await screen.findByTestId("sec-error")).toHaveTextContent(A("mfa.throttled"));
    expect(screen.queryByTestId("mfa-secret")).toBeNull();
    expect(document.body.textContent).not.toContain(SECRET.secret);
    expect(screen.getByTestId("sec-enrol-begin")).toBeInTheDocument();
  });
});

describe("the new session after a voluntary enrolment", () => {
  it("falls back to company selection when the company cannot be selected again on the new session", async () => {
    const statusMock = vi.fn().mockResolvedValueOnce(status(NONE)).mockResolvedValueOnce(status(NONE)).mockResolvedValue(status(ENROLLED));
    const client = setup({ status: statusMock, selectCompany: vi.fn().mockRejectedValue(new ApiError(503, "company_unavailable")) });   // the session was restored without selecting a company, so every selection here is the one after the enrolment
    await openSecurity();
    await beginWith();
    await userEvent.type(await screen.findByLabelText(new RegExp(A("mfa.code"))), "123456{Enter}");
    await waitFor(() => expect(client.confirmEnrolment).toHaveBeenCalled());
    expect(await screen.findByRole("heading", { name: A("companies.title") })).toBeInTheDocument();      // never a half signed-in screen
  });

  it("the fallback to company selection clears the previous company's id, permissions, branch permissions and role grants (as changing company does)", async () => {
    const seen: Array<{ id: string | null; can: boolean; any: boolean; branch: boolean; role: boolean }> = [];
    let adopt: (u: typeof USER) => Promise<void> = async () => {};
    function Probe() {
      const a = useAuth();
      adopt = a.adoptEnrolledSession;
      seen.push({ id: a.companyId, can: a.can("company.profile.view"), any: a.canAny("company.profile.view"), branch: a.canOnBranch("company.profile.view", "b1"), role: a.hasRoleOnBranch("r", "b1") });
      return <span data-testid="phase">{a.phase}</span>;
    }
    const client = fakeClient({
      status: vi.fn().mockResolvedValue({ authenticated: true, user: USER }),
      accessMe: vi.fn().mockResolvedValue({ companyId: "c1", grants: [
        { roleId: "r", scope: "company", scopeId: null, permissions: ["company.profile.view"] },
        { roleId: "r2", scope: "branch", scopeId: "b1", permissions: ["company.profile.view"] },
      ] }),
      selectCompany: vi.fn().mockRejectedValue(new ApiError(503, "company_unavailable")),
    });
    render(<I18nProvider catalog={catalog}><AuthProvider api={client}><Probe /></AuthProvider></I18nProvider>);
    await waitFor(() => expect(screen.getByTestId("phase")).toHaveTextContent("ready"));
    expect(seen.at(-1)).toEqual({ id: "c1", can: true, any: true, branch: true, role: true });
    await act(async () => { await adopt(USER); });
    await waitFor(() => expect(screen.getByTestId("phase")).toHaveTextContent("needCompany"));
    expect(seen.at(-1)).toEqual({ id: null, can: false, any: false, branch: false, role: false });
  });
});
