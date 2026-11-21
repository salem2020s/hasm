import { fireEvent, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { catalog } from "./messages";
import { apiError, fakeClient, renderApp, USER } from "./test-utils";

beforeEach(() => {
  localStorage.clear();
  window.location.hash = "";
});

const stepRequired = { step: { kind: "mfa_required", challenge: "chal-1" } };
const stepEnrol = { step: { kind: "mfa_enrolment_required", challenge: "chal-2" } };

async function passwordStep() {
  await userEvent.type(await screen.findByLabelText(/اسم المستخدم/), "amal");
  await userEvent.type(screen.getByLabelText(/كلمة المرور/), "secret-password");
  await userEvent.click(screen.getByRole("button", { name: "دخول" }));
}

describe("the second step of a login (Phase 4.1)", () => {
  it("asks for the code after the password, sends the challenge with it, and enters the app on success", async () => {
    const client = fakeClient({ login: vi.fn().mockResolvedValue(stepRequired), verifyMfa: vi.fn().mockResolvedValue({ user: USER }) });
    renderApp(client);
    await passwordStep();
    expect(await screen.findByRole("heading", { name: "التحقق بخطوتين" })).toBeInTheDocument();
    expect(client.listCompanies).not.toHaveBeenCalled();                                                  // no session yet: nothing of the app was requested
    const code = screen.getByLabelText(/رمز التحقق/);
    expect(code).toHaveAttribute("autocomplete", "one-time-code");
    await userEvent.type(code, "123456{Enter}");                                                           // the keyboard alone completes the step
    expect(client.verifyMfa).toHaveBeenCalledWith("chal-1", "123456");
    expect(await screen.findByTestId("company-name")).toHaveTextContent("شركة الأمل");
  });

  it("shows one message for a wrong code, clears the field, stays on the step and can go back to the password form", async () => {
    const client = fakeClient({ login: vi.fn().mockResolvedValue(stepRequired), verifyMfa: vi.fn().mockRejectedValue(apiError(401, "invalid_credentials")) });
    renderApp(client);
    await passwordStep();
    await userEvent.type(await screen.findByLabelText(/رمز التحقق/), "000000{Enter}");
    expect(await screen.findByText(/الرمز غير صحيح أو انتهت صلاحية الخطوة/)).toBeInTheDocument();
    expect(screen.getByLabelText(/رمز التحقق/)).toHaveValue("");
    await userEvent.click(screen.getByRole("button", { name: "رجوع إلى تسجيل الدخول" }));
    expect(await screen.findByRole("heading", { name: "تسجيل الدخول" })).toBeInTheDocument();
    expect(client.listCompanies).not.toHaveBeenCalled();
  });

  it("reports throttling distinctly and validates the empty field without calling the API", async () => {
    const client = fakeClient({ login: vi.fn().mockResolvedValue(stepRequired), verifyMfa: vi.fn().mockRejectedValue(apiError(429, "too_many_attempts")) });
    renderApp(client);
    await passwordStep();
    await userEvent.click(await screen.findByRole("button", { name: "تحقق" }));
    expect(screen.getByText("هذا الحقل مطلوب.")).toBeInTheDocument();
    expect(client.verifyMfa).not.toHaveBeenCalled();
    await userEvent.type(screen.getByLabelText(/رمز التحقق/), "111111{Enter}");
    expect(await screen.findByText(/محاولات كثيرة/)).toBeInTheDocument();
  });

  it("is English and left-to-right when the language is switched on the second step", async () => {
    renderApp(fakeClient({ login: vi.fn().mockResolvedValue(stepRequired) }));
    await passwordStep();
    await screen.findByRole("heading", { name: "التحقق بخطوتين" });
    fireEvent.click(screen.getByRole("button", { name: "English" }));
    expect(await screen.findByRole("heading", { name: "Two-step verification" })).toBeInTheDocument();
    expect(document.documentElement.dir).toBe("ltr");
    expect(screen.getByLabelText(/Verification code/)).toBeInTheDocument();
  });
});

describe("the enrolment of a required authenticator and the recovery notice", () => {
  it("takes the one-time code, shows the secret once, confirms with a code and enters the app", async () => {
    const client = fakeClient({
      login: vi.fn().mockResolvedValue(stepEnrol),
      beginEnrolment: vi.fn().mockResolvedValue({ otpauthUri: "otpauth://totp/HASM:amal?secret=JBSWY3DPEHPK3PXP", secret: "JBSWY3DPEHPK3PXP" }),
      confirmEnrolment: vi.fn().mockResolvedValue({ user: USER }),
    });
    renderApp(client);
    await passwordStep();
    expect(await screen.findByRole("heading", { name: "إعداد تطبيق المصادقة" })).toBeInTheDocument();
    await userEvent.type(screen.getByLabelText(/رمز التسجيل لمرة واحدة/), "ABCD-EFGH{Enter}");
    expect(client.beginEnrolment).toHaveBeenCalledWith({ challenge: "chal-2", enrolmentCode: "ABCD-EFGH" });
    expect(await screen.findByTestId("mfa-secret")).toHaveTextContent("JBSWY3DPEHPK3PXP");
    expect(screen.getByTestId("mfa-uri")).toHaveAttribute("href", "otpauth://totp/HASM:amal?secret=JBSWY3DPEHPK3PXP");
    await userEvent.type(screen.getByLabelText(/رمز التحقق/), "654321{Enter}");
    expect(client.confirmEnrolment).toHaveBeenCalledWith({ challenge: "chal-2", code: "654321" });
    expect(await screen.findByTestId("company-name")).toHaveTextContent("شركة الأمل");
  });

  it("an invalid enrolment code shows one message and no secret", async () => {
    const client = fakeClient({ login: vi.fn().mockResolvedValue(stepEnrol), beginEnrolment: vi.fn().mockRejectedValue(apiError(401, "invalid_credentials")) });
    renderApp(client);
    await passwordStep();
    await userEvent.type(await screen.findByLabelText(/رمز التسجيل لمرة واحدة/), "WRONG{Enter}");
    expect(await screen.findByText(/رمز التسجيل غير صالح أو منتهي الصلاحية/)).toBeInTheDocument();
    expect(screen.queryByTestId("mfa-secret")).not.toBeInTheDocument();
  });

  it("a wrong confirmation code keeps the secret on screen and shows the message", async () => {
    const client = fakeClient({
      login: vi.fn().mockResolvedValue(stepEnrol),
      beginEnrolment: vi.fn().mockResolvedValue({ otpauthUri: "otpauth://totp/x?secret=AAAA", secret: "AAAA" }),
      confirmEnrolment: vi.fn().mockRejectedValue(apiError(401, "invalid_credentials")),
    });
    renderApp(client);
    await passwordStep();
    await userEvent.type(await screen.findByLabelText(/رمز التسجيل لمرة واحدة/), "CODE{Enter}");
    await userEvent.type(await screen.findByLabelText(/رمز التحقق/), "000000{Enter}");
    expect(await screen.findByText(/الرمز غير صحيح أو انتهت صلاحية الخطوة/)).toBeInTheDocument();
    expect(screen.getByTestId("mfa-secret")).toHaveTextContent("AAAA");
  });

  it("a user who needs recovery sees only the notice: no code field, no company request", async () => {
    const client = fakeClient({ login: vi.fn().mockResolvedValue({ step: { kind: "mfa_recovery_required" } }) });
    renderApp(client);
    await passwordStep();
    expect(await screen.findByTestId("mfa-recovery")).toBeInTheDocument();
    expect(screen.queryByLabelText(/رمز/)).not.toBeInTheDocument();
    expect(client.listCompanies).not.toHaveBeenCalled();
    await userEvent.click(screen.getByRole("button", { name: "رجوع إلى تسجيل الدخول" }));
    expect(await screen.findByRole("heading", { name: "تسجيل الدخول" })).toBeInTheDocument();
  });
});

describe("the catalogs", () => {
  it("carry exactly the same mfa keys in Arabic and English", () => {
    const ar = Object.keys(catalog.ar).filter((k) => k.startsWith("mfa.")).sort();
    const en = Object.keys(catalog.en).filter((k) => k.startsWith("mfa.")).sort();
    expect(ar.length).toBeGreaterThan(10);
    expect(ar).toEqual(en);
  });
});
