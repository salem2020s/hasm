import { fireEvent, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { apiError, renderApp, signedIn } from "./test-utils";

const go = async (hash: string) => {
  await screen.findByTestId("company-name");
  window.location.hash = hash;
  fireEvent(window, new HashChangeEvent("hashchange"));
};

const syp = {
  currencies: [{ code: "SYP", nameEn: "Syrian Pound", minorUnits: 2, isBase: true, enabled: true, revision: 1 }], baseCurrency: "SYP", baseCurrencySelected: true,
  baseCurrencyLock: { locked: false, checksImplemented: false, unimplementedDomains: [] },
};
const settings = {
  settings: { defaultLanguage: "ar", dateFormat: "yyyy-MM-dd", numeralSystem: "latin", timeZone: "Asia/Damascus" }, revision: 1, exists: true, setupComplete: true,
  updatedAtUtc: null, options: { languages: ["ar", "en"], dateFormats: ["yyyy-MM-dd"], numeralSystems: ["latin"] },
};

beforeEach(() => { localStorage.clear(); window.location.hash = ""; });

describe("dashboard (real company information only)", () => {
  it("shows the selected company, the configured base currency, setup status and the edition from the real APIs", async () => {
    renderApp(signedIn({ getCurrencies: vi.fn().mockResolvedValue(syp), getSettings: vi.fn().mockResolvedValue(settings) }));
    expect(await screen.findByTestId("metric-base-currency")).toHaveTextContent("SYP");
    expect(screen.getByTestId("metric-company")).toHaveTextContent("شركة الأمل");
    expect(screen.getByTestId("metric-edition")).toHaveTextContent("تجريبية");
    // profile not entered yet (default mock) => 2 of 3 steps are done
    await waitFor(() => expect(screen.getByTestId("metric-setup")).toHaveTextContent("2 / 3"));
  });

  it("shows no financial or inventory figures: the future widgets are identified empty states without any number", async () => {
    renderApp(signedIn());
    const widgets = await screen.findByTestId("future-widgets");
    expect(within(widgets).getAllByText("غير متاح بعد")).toHaveLength(2);
    expect(widgets.textContent).not.toMatch(/\d/);
    expect(document.body.textContent).not.toMatch(/SAMPLE|عينات|بيانات تصميم/);
  });

  it("shows only what the user may see and never calls APIs the permission does not allow", async () => {
    const client = signedIn({}, ["company.profile.view"]);
    renderApp(client);
    await screen.findByTestId("metric-company");
    expect(screen.queryByTestId("metric-base-currency")).toBeNull();
    expect(screen.queryByTestId("metric-edition")).toBeNull();
    expect(screen.getByRole("button", { name: /^بيانات وإعدادات الشركة/ })).toBeInTheDocument(); // quick access: only permitted screens
    expect(screen.queryByRole("button", { name: /^العملات/ })).toBeNull();
    expect(client.getCurrencies).not.toHaveBeenCalled();
    expect(client.getSettings).not.toHaveBeenCalled();
    expect(client.getEntitlements).not.toHaveBeenCalled();
  });

  const profileDone = { profile: { nameAr: "شركة الأمل", countryCode: "SY" }, revision: 1, updatedAtUtc: null, projection: "synced" };
  const complete = () => ({ getProfile: vi.fn().mockResolvedValue(profileDone), getCurrencies: vi.fn().mockResolvedValue(syp), getSettings: vi.fn().mockResolvedValue(settings) });
  const entry = { id: 7, entity: "profile", key: "", revision: 3, actorUserId: "u1", changedAtUtc: "2026-11-11T21:05:00Z", auditSeq: 7,
    changes: { city: { old: "SECRET-OLD", new: "SECRET-NEW" }, taxRegistrationNo: { changed: true } } };

  it("incomplete setup shows the checklist with links and the progress KPI, and no recent-activity card", async () => {
    renderApp(signedIn({ getCurrencies: vi.fn().mockResolvedValue(syp), getSettings: vi.fn().mockResolvedValue(settings) })); // profile missing
    expect(await screen.findByRole("button", { name: "إدخال ملف الشركة" })).toBeInTheDocument();
    expect(screen.getByTestId("metric-setup")).toHaveTextContent("2 / 3");
    expect(screen.queryByTestId("recent-activity")).toBeNull();
    await userEvent.click(screen.getByRole("button", { name: "إدخال ملف الشركة" }));
    await waitFor(() => expect(window.location.hash).toBe("#/company/details"));
  });

  it("completed setup hides the whole checklist and the 3/3 KPI, and shows real recent activity instead (no field values)", async () => {
    renderApp(signedIn({ ...complete(), getHistory: vi.fn().mockResolvedValue({ entries: [entry] }) }));
    const activity = await screen.findByTestId("recent-activity");
    expect(within(activity).getByText("ملف الشركة")).toBeInTheDocument();
    expect(activity.querySelector("time")).toHaveTextContent("2026-11-12 00:05"); // company zone + format, display only
    expect(screen.queryByRole("button", { name: "إدخال ملف الشركة" })).toBeNull();
    expect(screen.queryByTestId("metric-setup")).toBeNull();
    expect(document.body.textContent).not.toMatch(/3 \/ 3|SECRET-OLD|SECRET-NEW/); // sensitive/old/new values are never shown
  });

  it("completed setup without configuration-history permission shows no activity card and never calls the history API", async () => {
    const client = signedIn(complete(), ["company.profile.view", "company.settings.view", "company.currencies.view"]);
    renderApp(client);
    await screen.findByTestId("metric-base-currency");
    expect(screen.queryByTestId("recent-activity")).toBeNull();
    expect(screen.queryByText("آخر نشاط")).toBeNull();
    expect(client.getHistory).not.toHaveBeenCalled();
  });

  it("recent activity shows an empty state, and degrades gracefully when the history cannot be loaded", async () => {
    const a = renderApp(signedIn({ ...complete(), getHistory: vi.fn().mockResolvedValue({ entries: [] }) }));
    expect(await screen.findByText("لا توجد تغييرات مسجّلة بعد.")).toBeInTheDocument();
    a.unmount();
    renderApp(signedIn({ ...complete(), getHistory: vi.fn().mockRejectedValue(apiError(503, "company_unavailable")) }));
    expect(await screen.findByTestId("activity-unavailable")).toBeInTheDocument();
    expect(screen.getByTestId("metric-base-currency")).toHaveTextContent("SYP"); // the rest of the dashboard is unaffected
  });

  it("quick access opens the existing company screens", async () => {
    renderApp(signedIn());
    await userEvent.click(await screen.findByRole("button", { name: /^العملات/ }));
    await waitFor(() => expect(window.location.hash).toBe("#/company/currencies"));
  });
});

describe("development-only design previews", () => {
  it("are reachable from the development menu, are clearly marked as non-functional sample content and call no business API", async () => {
    const client = signedIn();
    renderApp(client);
    await screen.findByTestId("metric-base-currency"); // the dashboard has loaded its own data
    const business = [client.getProfile, client.getCurrencies, client.getHistory, client.putProfile, client.setCurrency];
    business.forEach((f) => (f as ReturnType<typeof vi.fn>).mockClear());
    await go("#/dev/preview/warehouses");
    expect(await screen.findByTestId("preview-warehouses")).toBeInTheDocument();
    expect(screen.getByTestId("design-preview-banner")).toHaveTextContent("معاينة تصميم");
    expect(screen.getAllByText("بيانات تصميم توضيحية").length).toBeGreaterThan(3);
    for (const api of business) expect(api).not.toHaveBeenCalled();
    // action buttons are inert
    await userEvent.click(screen.getByRole("button", { name: "إضافة صنف" }));
    expect(screen.getByRole("button", { name: "إضافة صنف" })).toHaveAttribute("aria-disabled", "true");
    expect(screen.getByRole("menuitem", { name: /معاينات التصميم/ })).toBeInTheDocument();
  });

  it("render the sales invoice preview with identifiers and dates kept left-to-right, and keep the route when the language changes", async () => {
    renderApp(signedIn());
    await go("#/dev/preview/sales-invoices");
    const sales = await screen.findByTestId("preview-sales");
    expect(within(sales).getByText("INV-000128").className).toContain("ltr-value");
    expect(within(sales).getByText("2026-11-11").className).toContain("ltr-value");
    fireEvent.click(screen.getByRole("button", { name: "English" }));
    expect(await screen.findByText("Sales invoices")).toBeInTheDocument();
    expect(document.documentElement.dir).toBe("ltr");
    expect(window.location.hash).toBe("#/dev/preview/sales-invoices");
    expect(screen.getByTestId("company-name")).toHaveTextContent("Amal Co");
    expect(screen.getByTestId("design-preview-banner")).toHaveTextContent("DESIGN PREVIEW");
  });

  it("render the module header gallery for all five modules", async () => {
    renderApp(signedIn());
    await go("#/dev/preview");
    const gallery = await screen.findByTestId("preview-gallery");
    for (const name of ["المحاسبة", "المستودعات", "المبيعات", "المشتريات", "التقارير"]) expect(within(gallery).getByRole("heading", { name })).toBeInTheDocument();
  });
});
