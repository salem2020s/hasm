import { fireEvent, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { apiError, fakeClient, renderApp, signedIn, USER } from "./test-utils";

beforeEach(() => {
  localStorage.clear();
  window.location.hash = "";
});

describe("login, session and company selection", () => {
  it("shows the login form (Arabic, RTL) when there is no session", async () => {
    renderApp(fakeClient());
    expect(await screen.findByRole("heading", { name: "تسجيل الدخول" })).toBeInTheDocument();
    expect(document.documentElement.dir).toBe("rtl");
    expect(screen.getByLabelText(/اسم المستخدم/)).toBeInTheDocument();
    expect(screen.getByLabelText(/كلمة المرور/)).toHaveAttribute("type", "password");
  });

  it("validates required fields without calling the API", async () => {
    const client = fakeClient();
    renderApp(client);
    await userEvent.click(await screen.findByRole("button", { name: "دخول" }));
    expect(screen.getAllByText("هذا الحقل مطلوب.")).toHaveLength(2);
    expect(client.login).not.toHaveBeenCalled();
  });

  it("logs in, lists only the member's companies with the locale name, and enters the selected one", async () => {
    const client = fakeClient({ listCompanies: vi.fn().mockResolvedValue({ companies: [{ id: "c1", nameAr: "شركة الأمل", nameEn: "Amal Co" }, { id: "c2", nameAr: "مؤسسة النور", nameEn: "Al Nour" }] }) });
    renderApp(client);
    await userEvent.type(await screen.findByLabelText(/اسم المستخدم/), "amal");
    await userEvent.type(screen.getByLabelText(/كلمة المرور/), "secret-password");
    await userEvent.click(screen.getByRole("button", { name: "دخول" }));
    expect(client.login).toHaveBeenCalledWith("amal", "secret-password");
    await userEvent.click(await screen.findByRole("button", { name: "شركة الأمل" }));
    expect(client.selectCompany).toHaveBeenCalledWith("c1");
    expect(await screen.findByTestId("company-name")).toHaveTextContent("شركة الأمل");
  });

  it("after login with exactly one accessible company, enters it automatically through the select-company API (no selection screen)", async () => {
    const client = fakeClient();
    renderApp(client);
    await userEvent.type(await screen.findByLabelText(/اسم المستخدم/), "amal");
    await userEvent.type(screen.getByLabelText(/كلمة المرور/), "secret-password");
    await userEvent.click(screen.getByRole("button", { name: "دخول" }));
    expect(await screen.findByTestId("company-name")).toHaveTextContent("شركة الأمل");
    expect(client.selectCompany).toHaveBeenCalledWith("c1"); // the real API call, validated by the server
    expect(screen.queryByRole("heading", { name: "اختيار الشركة" })).toBeNull();
  });

  it("with several accessible companies after login, always shows the selection screen and selects nothing by itself", async () => {
    const client = fakeClient({ listCompanies: vi.fn().mockResolvedValue({ companies: [{ id: "c1", nameAr: "أ", nameEn: "A" }, { id: "c2", nameAr: "ب", nameEn: "B" }] }) });
    renderApp(client);
    await userEvent.type(await screen.findByLabelText(/اسم المستخدم/), "amal");
    await userEvent.type(screen.getByLabelText(/كلمة المرور/), "secret-password");
    await userEvent.click(screen.getByRole("button", { name: "دخول" }));
    expect(await screen.findByRole("heading", { name: "اختيار الشركة" })).toBeInTheDocument();
    expect(client.selectCompany).not.toHaveBeenCalled();
  });

  it("if automatically entering the only company fails, falls back to the selection screen (it is not treated as 'no company') and choosing retries", async () => {
    const selectCompany = vi.fn().mockRejectedValueOnce(apiError(503, "company_unavailable")).mockResolvedValue({ companyId: "c1" });
    const client = fakeClient({ selectCompany });
    renderApp(client);
    await userEvent.type(await screen.findByLabelText(/اسم المستخدم/), "amal");
    await userEvent.type(screen.getByLabelText(/كلمة المرور/), "secret-password");
    await userEvent.click(screen.getByRole("button", { name: "دخول" }));
    expect(await screen.findByRole("heading", { name: "اختيار الشركة" })).toBeInTheDocument();
    expect(screen.queryByTestId("no-companies")).toBeNull();
    await userEvent.click(screen.getByRole("button", { name: "شركة الأمل" }));
    expect(await screen.findByTestId("company-name")).toHaveTextContent("شركة الأمل");
    expect(selectCompany).toHaveBeenCalledTimes(2);
  });

  it("restores a session without a selected company by entering the only accessible company", async () => {
    const accessMe = vi.fn().mockRejectedValueOnce(apiError(409, "company_not_selected")).mockResolvedValue({ companyId: "c1", grants: [] });
    const client = signedIn({ accessMe });
    renderApp(client);
    expect(await screen.findByTestId("company-name")).toBeInTheDocument();
    expect(client.selectCompany).toHaveBeenCalledWith("c1");
  });

  it("Change company always opens the selection screen, even with one company, and does not silently re-enter it", async () => {
    const client = signedIn();
    renderApp(client);
    await userEvent.click(await screen.findByRole("button", { name: "تغيير الشركة" }));
    expect(await screen.findByRole("heading", { name: "اختيار الشركة" })).toBeInTheDocument();
    expect(client.clearCompany).toHaveBeenCalled();
    expect(client.selectCompany).not.toHaveBeenCalled();
    await userEvent.click(screen.getByRole("button", { name: "شركة الأمل" }));
    expect(await screen.findByTestId("company-name")).toBeInTheDocument();
    expect(client.selectCompany).toHaveBeenCalledWith("c1");
  });

  it("falls back to the other language name, and never shows an identifier for an unnamed company", async () => {
    const client = fakeClient({ listCompanies: vi.fn().mockResolvedValue({ companies: [{ id: "c1", nameAr: null, nameEn: "Only English" }, { id: "c-secret-id", nameAr: null, nameEn: null }] }) });
    renderApp(client);
    await userEvent.type(await screen.findByLabelText(/اسم المستخدم/), "a");
    await userEvent.type(screen.getByLabelText(/كلمة المرور/), "b");
    await userEvent.click(screen.getByRole("button", { name: "دخول" }));
    expect(await screen.findByRole("button", { name: "Only English" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /شركة 2/ })).toBeInTheDocument();
    expect(document.body.textContent).not.toContain("c-secret-id");
  });

  it("shows invalid credentials and a throttling message, and clears the password", async () => {
    const login = vi.fn().mockRejectedValueOnce(apiError(401, "invalid_credentials")).mockRejectedValueOnce(apiError(429, "too_many_attempts", { retryAfterSeconds: 30 }));
    renderApp(fakeClient({ login }));
    const user = await screen.findByLabelText(/اسم المستخدم/);
    await userEvent.type(user, "amal");
    await userEvent.type(screen.getByLabelText(/كلمة المرور/), "wrong");
    await userEvent.click(screen.getByRole("button", { name: "دخول" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("اسم المستخدم أو كلمة المرور غير صحيحة.");
    expect(screen.getByLabelText(/كلمة المرور/)).toHaveValue("");
    await userEvent.type(screen.getByLabelText(/كلمة المرور/), "again");
    await userEvent.click(screen.getByRole("button", { name: "دخول" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("30");
  });

  it("restores a live session and the selected company without asking for the password", async () => {
    renderApp(signedIn());
    expect(await screen.findByTestId("company-name")).toHaveTextContent("شركة الأمل");
    expect(screen.queryByLabelText(/كلمة المرور/)).toBeNull();
  });

  it("asks for a company when the session has none selected and several are accessible", async () => {
    const client = signedIn({ accessMe: vi.fn().mockRejectedValue(apiError(409, "company_not_selected")), listCompanies: vi.fn().mockResolvedValue({ companies: [{ id: "c1", nameAr: "أ", nameEn: "A" }, { id: "c2", nameAr: "ب", nameEn: "B" }] }) });
    renderApp(client);
    expect(await screen.findByRole("heading", { name: "اختيار الشركة" })).toBeInTheDocument();
  });

  it("reports an empty company list", async () => {
    renderApp(signedIn({ accessMe: vi.fn().mockRejectedValue(apiError(409, "company_not_selected")), listCompanies: vi.fn().mockResolvedValue({ companies: [] }) }));
    expect(await screen.findByTestId("no-companies")).toBeInTheDocument();
  });

  describe("Add New Company (visible, not operational)", () => {
    // The selection screen with the given companies is reached through "Change company" (which never auto-selects).
    const noCompany = (companies: unknown[]) => signedIn({ listCompanies: vi.fn().mockResolvedValue({ companies }) });
    const openSelection = async () => { await userEvent.click(await screen.findByRole("button", { name: "تغيير الشركة" })); };
    const one = [{ id: "c1", nameAr: "شركة الأمل", nameEn: "Amal Co" }];

    it("with one existing company: shows the action, explains the Trial limit, and creates nothing", async () => {
      const client = noCompany(one);
      renderApp(client);
      await openSelection();
      const add = await screen.findByRole("button", { name: /إضافة شركة جديدة/ });
      expect(add).toHaveAttribute("aria-disabled", "true");
      expect(add).toHaveAccessibleDescription(/غير متاح بعد/);
      expect(add).toHaveAccessibleDescription(/النسخة التجريبية تتضمن شركة واحدة/);
      expect(add).toHaveAccessibleDescription(/رخصة مدفوعة/);
      await userEvent.click(add);
      expect(client.selectCompany).not.toHaveBeenCalled();
      expect(screen.getByRole("heading", { name: "اختيار الشركة" })).toBeInTheDocument(); // still on the selection screen
      // no payment / upgrade workflow is suggested
      expect(screen.getByTestId("add-company").querySelector("a, [href]")).toBeNull();
      expect(document.body.textContent).not.toMatch(/اشترِ|ترقية|اشتراك|Upgrade|Buy|Purchase/);
    });

    it("is reachable and inert by keyboard, and never reads license data it does not have", async () => {
      const client = noCompany(one);
      renderApp(client);
      await screen.findByTestId("metric-edition"); // the dashboard has finished its own license read
      (client.getEntitlements as ReturnType<typeof vi.fn>).mockClear();
      await openSelection();
      const add = await screen.findByRole("button", { name: /إضافة شركة جديدة/ });
      add.focus();
      expect(add).toHaveFocus();
      await userEvent.keyboard("{Enter}");
      await userEvent.keyboard(" ");
      expect(client.selectCompany).not.toHaveBeenCalled();
      expect(client.getEntitlements).not.toHaveBeenCalled(); // license information is unavailable here and is not assumed
    });

    it("with an empty company list: still shows the action, with the general license note only", async () => {
      renderApp(noCompany([]));
      await openSelection();
      expect(await screen.findByTestId("no-companies")).toBeInTheDocument();
      const add = screen.getByRole("button", { name: /إضافة شركة جديدة/ });
      expect(add).toHaveAccessibleDescription(/تحدد الرخصة عدد الشركات/);
      expect(add).not.toHaveAccessibleDescription(/شركة واحدة/);
    });

    it("is English and left-to-right when the language is switched, keeping the selection screen", async () => {
      renderApp(noCompany(one));
      await openSelection();
      await screen.findByRole("button", { name: /إضافة شركة جديدة/ });
      fireEvent.click(screen.getByRole("button", { name: "English" }));
      const add = await screen.findByRole("button", { name: /Add New Company/ });
      expect(document.documentElement.dir).toBe("ltr");
      expect(add).toHaveAccessibleDescription(/Trial edition includes one company; additional companies require a paid license/);
      expect(screen.getByRole("heading", { name: "Select a company" })).toBeInTheDocument();
    });
  });

  it("signs out securely and shows the login form", async () => {
    const client = signedIn();
    renderApp(client);
    await userEvent.click(await screen.findByRole("button", { name: "تسجيل الخروج" }));
    expect(client.logout).toHaveBeenCalled();
    expect(await screen.findByTestId("notice-logged-out")).toBeInTheDocument();
    expect(screen.queryByTestId("company-name")).toBeNull();
  });

  it("ends the session UI when the server reports it expired (401 from a page call)", async () => {
    const client = signedIn({ getProfile: vi.fn().mockRejectedValue(apiError(401, "unauthenticated")) });
    renderApp(client);
    await screen.findByTestId("company-name");
    window.location.hash = "#/company/profile";
    fireEvent(window, new HashChangeEvent("hashchange"));
    expect(await screen.findByTestId("notice-expired")).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "تسجيل الدخول" })).toBeInTheDocument();
  });

  it("reports an unreachable backend", async () => {
    renderApp(fakeClient({ status: vi.fn().mockRejectedValue(new Error("down")) }));
    expect(await screen.findByTestId("unreachable")).toBeInTheDocument();
  });

  it("stores no session, password, token or permission data in browser storage", async () => {
    const setItem = vi.spyOn(Storage.prototype, "setItem");
    const client = fakeClient();
    renderApp(client);
    await userEvent.type(await screen.findByLabelText(/اسم المستخدم/), "amal");
    await userEvent.type(screen.getByLabelText(/كلمة المرور/), "secret-password");
    await userEvent.click(screen.getByRole("button", { name: "دخول" }));
    await screen.findByTestId("company-name"); // the only company is entered automatically
    fireEvent.click(screen.getByRole("button", { name: "English" }));
    for (const call of setItem.mock.calls) expect(call[0]).toBe("hasm.locale");
    const dump = JSON.stringify({ ...localStorage }) + JSON.stringify({ ...sessionStorage });
    expect(dump).not.toMatch(/secret-password|token|company\./);
  });
});

describe("application shell", () => {
  it("shows the official mark unchanged with an empty alt (the adjacent text names it)", async () => {
    const { container } = renderApp(signedIn());
    await screen.findByTestId("company-name");
    const img = container.querySelector(".sidebar-brand img")!;
    expect(img.getAttribute("src")).toContain("hasm-mark");
    expect(img.getAttribute("alt")).toBe("");
    expect(img).toHaveAttribute("width", "120");
    expect(img).toHaveAttribute("height", "136");
  });

  it("switches Arabic RTL <-> English LTR including the company display name fallback", async () => {
    renderApp(signedIn());
    expect(await screen.findByTestId("company-name")).toHaveTextContent("شركة الأمل");
    fireEvent.click(screen.getByRole("button", { name: "English" }));
    expect(document.documentElement.dir).toBe("ltr");
    expect(document.documentElement.lang).toBe("en");
    expect(screen.getByTestId("company-name")).toHaveTextContent("Amal Co");
    expect(screen.getByRole("button", { name: "Sign out" })).toBeInTheDocument();
  });

  it("places the sidebar at the inline-start edge and mirrors it immediately on language switch, keeping route and company", async () => {
    const { container } = renderApp(signedIn());
    await screen.findByTestId("company-name");
    const shell = container.querySelector(".app-shell")!;
    // The sidebar is the first grid child; with dir=rtl it is laid out on the RIGHT, with dir=ltr on the LEFT (CSS grid follows the document direction).
    expect(shell.firstElementChild).toBe(screen.getByTestId("sidebar"));
    expect(document.documentElement.dir).toBe("rtl");
    await userEvent.hover(screen.getByRole("menuitem", { name: /إدارة الشركة/ }));
    await userEvent.click(await screen.findByRole("menuitem", { name: "بيانات وإعدادات الشركة" }));
    await waitFor(() => expect(window.location.hash).toBe("#/company/details"));
    fireEvent.click(screen.getByRole("button", { name: "English" }));
    expect(document.documentElement.dir).toBe("ltr");
    expect(shell.firstElementChild).toBe(screen.getByTestId("sidebar"));
    expect(container.querySelector(".app-shell")).toBe(shell); // same DOM: no remount, no logout
    expect(screen.getByTestId("company-name")).toHaveTextContent("Amal Co");
    expect(window.location.hash).toBe("#/company/details");
    expect(screen.getByRole("menuitem", { name: "Home" })).toBeInTheDocument();
  });

  it("lists the planned modules; unimplemented ones are disabled Coming soon items that never navigate", async () => {
    renderApp(signedIn());
    await screen.findByTestId("company-name");
    for (const name of ["المحاسبة", "المستودعات", "المبيعات", "المشتريات", "التقارير", "الإعدادات"]) {
      const item = screen.getByRole("menuitem", { name: new RegExp(name) });
      expect(item).toHaveAttribute("aria-disabled", "true");
      expect(item).toHaveTextContent("قريباً");
      await userEvent.click(item);
      fireEvent.keyDown(item, { key: "Enter" });
      expect(window.location.hash).toBe("");
    }
  });

  it("navigates only by click: hovering a menu expands it without changing the page", async () => {
    renderApp(signedIn());
    await screen.findByTestId("company-name");
    const company = screen.getByRole("menuitem", { name: /الشركة/ });
    await userEvent.hover(company);
    const profile = await screen.findByRole("menuitem", { name: "بيانات وإعدادات الشركة" });
    expect(window.location.hash).toBe("");
    await userEvent.hover(profile);
    expect(window.location.hash).toBe("");
    await userEvent.click(profile);
    await waitFor(() => expect(window.location.hash).toBe("#/company/details"));
    expect(await screen.findByRole("heading", { name: "بيانات وإعدادات الشركة" })).toBeInTheDocument();
  });

  it("hides menu entries and refuses the route when the permission is missing (protected navigation)", async () => {
    const client = signedIn({}, ["company.profile.view"]);
    renderApp(client);
    await screen.findByTestId("company-name");
    await userEvent.click(screen.getByRole("menuitem", { name: /الشركة/ }));
    expect(screen.queryByRole("menuitem", { name: "العملات" })).toBeNull();
    window.location.hash = "#/company/currencies";
    fireEvent(window, new HashChangeEvent("hashchange"));
    expect(await screen.findByTestId("forbidden")).toBeInTheDocument();
    expect(client.getCurrencies).not.toHaveBeenCalled();
  });

  it("shows a not-found message for an unknown route", async () => {
    renderApp(signedIn());
    await screen.findByTestId("company-name");
    window.location.hash = "#/nope";
    fireEvent(window, new HashChangeEvent("hashchange"));
    expect(await screen.findByTestId("not-found")).toBeInTheDocument();
  });

  it("lets the user change company", async () => {
    const client = signedIn();
    renderApp(client);
    await userEvent.click(await screen.findByRole("button", { name: "تغيير الشركة" }));
    expect(client.clearCompany).toHaveBeenCalled();
    expect(await screen.findByRole("heading", { name: "اختيار الشركة" })).toBeInTheDocument();
  });

  it("shows the setup checklist from the real APIs on the home page", async () => {
    renderApp(signedIn());
    expect(await screen.findByRole("button", { name: "إدخال ملف الشركة" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "تحديد العملة الأساسية" })).toBeInTheDocument();
    expect(USER.login).toBe("amal");
  });
});
